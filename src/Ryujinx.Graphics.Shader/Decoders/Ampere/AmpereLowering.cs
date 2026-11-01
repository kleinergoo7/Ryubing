using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Lowers native Ampere (SM86) SASS to Maxwell instructions understood by Ryujing.
    /// </summary>
    /// <remarks>
    /// Ported from Eglantine's <c>frontend/ampere/lower_to_maxwell.h</c>. Only operations whose
    /// semantics are representable by the existing Maxwell frontend are lowered. Scheduling words
    /// are added by <see cref="NativeShaderImage"/>, using Ryujing's code-relative layout.
    /// </remarks>
    public static partial class AmpereLowering
    {
        private readonly struct Relocation
        {
            public readonly uint OutputIndex;
            public readonly long Target;

            public Relocation(uint outputIndex, long target)
            {
                OutputIndex = outputIndex;
                Target = target;
            }
        }

        private readonly struct HalfSource
        {
            public readonly uint Reg;
            public readonly uint Swizzle;
            public readonly bool Absolute;
            public readonly bool Negate;

            public HalfSource(uint reg, uint swizzle, bool absolute, bool negate)
            {
                Reg = reg;
                Swizzle = swizzle;
                Absolute = absolute;
                Negate = negate;
            }
        }

        private static uint PopCount(uint value) => (uint)BitOperations.PopCount(value);

        public static AmpereLoweredProgram LowerToMaxwell(ReadOnlySpan<AmpereInstruction> code)
        {
            AmpereLoweredProgram result = new();
            List<Relocation> relocations = [];
            List<uint> callTargets = [];
            List<uint> callLinks = [];
            bool mainExited = false;
            bool computedBranch = false; // a jump table: the program ends at its self-branch, not an EXIT
            bool usesUniformBank = false;

            foreach (AmpereInstruction instruction in code)
            {
                uint op = instruction.Bits(0, 12);

                usesUniformBank |= op == 0x3c4 || op == 0x2bd || op == 0xc0c || op == 0x986 || op == 0x9a8 || op == 0xc12 ||
                                   op == 0xc17 || op == 0xe24 || op == 0xc24 || op == 0xd00 || op == 0xd09 || op == 0x9c3 ||
                                   op == 0xc45 || op == 0x2a4 || op == 0x891 || op == 0x890 || op == 0x290 || op == 0x28c ||
                                   op == 0x88c || op == 0x287 || op == 0x887 || op == 0x958 || op == 0xab9 || op == 0xabb ||
                                   op == 0x899 || op == 0xc20 || op == 0xe21 || op == 0xe23 || op == 0xc02 || op == 0x886 ||
                                   op == 0x3c2 || op == 0xc82 || op == 0x882 || op == 0x292 || op == 0x892 || op == 0x291 ||
                                   op == 0x981 || op == 0xc13 || op == 0xc10 || op == 0xc23 || op == 0x526 || op == 0x2be ||
                                   op == 0x8a4 || op == 0xd05 || (op == 0x984 && instruction.Bits(91, 1) != 0) ||
                                   op == 0x89c || op == 0x883 || op == 0x2a5 || op == 0xd06 || op == 0x299 || op == 0xc09 ||
                                   (op == 0x81c && instruction.Bits(67, 1) != 0);
            }

            AmpereLoweredProgram Fail(uint index, string reason)
            {
                result.Instructions.Clear();
                result.SourceInstructionOffsets.Clear();
                result.ErrorOffset = index * AmpereInstruction.Size;
                result.Error = reason;
                return result;
            }

            AmpereLoweredProgram Finish(uint index)
            {
                foreach (Relocation relocation in relocations)
                {
                    if (relocation.Target > index)
                    {
                        return Fail(index, "native branch or call leaves captured program");
                    }

                    uint target = result.SourceInstructionOffsets[(int)relocation.Target];
                    long relative = (long)AmpereLayout.AddressOf(target) -
                                    AmpereLayout.AddressOf(relocation.OutputIndex) - 8;

                    if (relative < -0x800000 || relative > 0x7fffff)
                    {
                        return Fail(index, "lowered branch distance");
                    }

                    result.Instructions[(int)relocation.OutputIndex] |= ((ulong)relative & 0xffffff) << 20;
                }

                result.SourceSize = (index + 1) * AmpereInstruction.Size;
                return result;
            }

            if (code.IsEmpty || code.Length > (int)AmpereProgramCapture.MaximumInstructions)
            {
                return Fail(0, "invalid native program size");
            }

            uint[] scratchCheckBits = [16, 24, 32, 64];

            for (uint index = 0; index < code.Length; ++index)
            {
                AmpereInstruction insn = code[(int)index];
                result.SourceInstructionOffsets.Add((uint)result.Instructions.Count);

                uint opcode = insn.Bits(0, 12);

                if (insn.Bits(12, 4) == 15)
                {
                    continue; // @!PT: never executes (compiler padding)
                }

                uint? activeLink = null;
                uint activeEntry = 0;

                if (mainExited)
                {
                    for (int call = 0; call < callTargets.Count; ++call)
                    {
                        if (callTargets[call] <= index && callTargets[call] >= activeEntry)
                        {
                            activeEntry = callTargets[call];
                            activeLink = callLinks[call];
                        }
                    }

                    // RET has no destination GPR; bits 16..23 are reserved, not a write to R0.
                    if (activeLink.HasValue && opcode != 0x950 && insn.Bits(16, 8) == activeLink.Value)
                    {
                        return Fail(index, "native callee overwrites link register");
                    }
                }

                // Scratch values and emulated uniform state occupy reserved high GPRs.
                // Reject native accesses that could alias that state.
                foreach (uint bit in scratchCheckBits)
                {
                    if (bit == 32 && insn.Bits(9, 3) != 1 && opcode != 0xb60 && opcode != 0xb66)
                    {
                        continue;
                    }

                    uint reg = insn.Bits((int)bit, 8);

                    if (reg >= (usesUniformBank ? 216U : 246U) && reg < 255)
                    {
                        return Fail(index, "native program uses lowering scratch registers");
                    }
                }

                // UR0-UR21 live in R216-R237 and UR22-UR27 in R240-R245 (R239 holds the uniform
                // predicates), so even/odd pairs stay adjacent. Sites that still check reg < 16 rely on
                // UR0-UR15 being R216-R231.
                uint UniformRegister(uint reg)
                {
                    if (reg < 22)
                    {
                        return 216 + reg;
                    }

                    if (reg < 28)
                    {
                        return 218 + reg;
                    }

                    if (reg == 63)
                    {
                        return 255;
                    }

                    return uint.MaxValue;
                }

                bool rewrittenUniform = false;
                bool uniformGuard = false;
                uint uniformOutput = 7; // ULOP3's UPn output: computed in P(6-n), then merged into R239

                if (opcode == 0x2a4 || opcode == 0x8a4 || opcode == 0x891 || opcode == 0x890 ||
                    opcode == 0x290 || opcode == 0x292 || opcode == 0x892 || opcode == 0x291)
                {
                    if (insn.Bits(12, 4) != 7 && opcode != 0x292 && opcode != 0x892)
                    {
                        return Fail(index, "native uniform arithmetic predication");
                    }

                    uniformGuard = insn.Bits(12, 4) != 7;
                    rewrittenUniform = true;

                    uint dst = UniformRegister(insn.Bits(16, 8));
                    uint a = UniformRegister(insn.Bits(24, 8));
                    uint c = UniformRegister(insn.Bits(64, 8));

                    if (dst == uint.MaxValue || a == uint.MaxValue || c == uint.MaxValue)
                    {
                        return Fail(index, "native uniform arithmetic register");
                    }

                    insn.W0 = (insn.W0 & 0xffff) | (dst << 16) | (a << 24);
                    insn.W2 = (insn.W2 & ~0x080000ffU) | c;

                    if (opcode == 0x2a4 || opcode == 0x290 || opcode == 0x292 || opcode == 0x291)
                    {
                        uint b = UniformRegister(insn.Bits(32, 8));

                        if (b == uint.MaxValue)
                        {
                            return Fail(index, "native uniform arithmetic second register");
                        }

                        insn.W1 = (insn.W1 & ~0xffU) | b;
                    }

                    if ((opcode == 0x292 || opcode == 0x892) && insn.Bits(80, 4) != 0xe)
                    {
                        if (insn.Bits(80, 1) != 0)
                        {
                            return Fail(index, "native uniform logic predicate output");
                        }

                        uniformOutput = insn.Bits(81, 3);
                        insn.W2 = (insn.W2 & ~(7U << 17)) | ((6 - uniformOutput) << 17);
                    }

                    opcode = opcode == 0x2a4 ? 0x224U :
                             opcode == 0x8a4 ? 0x824U :
                             opcode == 0x891 ? 0x811U :
                             opcode == 0x890 ? 0x810U :
                             opcode == 0x292 ? 0x212U :
                             opcode == 0x892 ? 0x812U :
                             opcode == 0x291 ? 0x211U : 0x210U;
                }

                bool uniformPredication = opcode == 0xabb || opcode == 0xab9 || opcode == 0xc82 || opcode == 0x882;
                uint nativePredicate = insn.Bits(12, 4);
                bool guardedUniform = (uniformPredication || uniformGuard) && nativePredicate != 7;

                // A uniform guard UPn lives in R239 bit 6-n and is swapped into P(6-n) for the op.
                uint guardSlot = 6 - (nativePredicate & 7);
                ulong predicate = (ulong)(guardedUniform ? ((nativePredicate & 8) | guardSlot) : nativePredicate) << 16;
                uint dest = insn.Bits(16, 8);

                ulong Base(ulong opcodeBits) => opcodeBits | predicate | dest;

                // NVN2 fullscreen vertex shaders read bank 24, which the game never binds (the console reads
                // zeros from an unbound bank); Maxwell has 18, so read it from bank 17, also never bound.
                uint Bank()
                {
                    uint value = insn.Bits(54, 5);

                    return value == 24 ? 17U : value;
                }

                ulong Cbuf()
                {
                    return ((ulong)(insn.Bits(38, 16) / 4) << 20) | ((ulong)Bank() << 34);
                }

                bool ValidCbuf()
                {
                    return insn.Bits(91, 1) == 0 && Bank() < 18 && (insn.Bits(38, 16) % 4) == 0;
                }

                string operandError = null;

                void Emit(ulong word) => result.Instructions.Add(word);

                // P(guard slot) while a uniform guard is swapped in; P6 for lowerings that use it as
                // scratch (R2UR, MATCH, HFMA2.RELU), which save and restore it with the defaults below.
                uint guardMask = guardSlot < 7 ? 1U << (int)guardSlot : 0U;

                void SavePredicate(uint mask = 1U << 6)
                {
                    Emit(0x38e8000000070000UL | 253 | (255UL << 8) | ((ulong)mask << 20));
                }

                void RestorePredicate(uint mask = 1U << 6)
                {
                    Emit(0x38f0000000070000UL | (253UL << 8) | ((ulong)mask << 20));
                }

                if (guardedUniform)
                {
                    SavePredicate(guardMask);
                    Emit(0x38f0000000070000UL | (239UL << 8) | ((ulong)guardMask << 20));
                }

                uint outputMask = uniformOutput == 7 ? 0U : 1U << (int)(6 - uniformOutput);

                if (outputMask != 0)
                {
                    // Keep the guest P(6-n) in R254 and start from the current UPn (a false guard keeps it).
                    Emit(0x38e8000000070000UL | 254 | (255UL << 8) | ((ulong)outputMask << 20));
                    Emit(0x38f0000000070000UL | (239UL << 8) | ((ulong)outputMask << 20));
                }

                // Pd = LUT(a, b, c) over predicate operands (index | negate << 3): each input becomes
                // 0 or ~0 (SEL), and LOP3 sets Pd when the result is nonzero.
                void PredicateLut(uint lut, uint a, uint b, uint c, uint target, bool uniformC = false)
                {
                    uint[] inputs = [a, b, c];

                    for (uint i = 0; i < 3; ++i)
                    {
                        if (i == 2 && uniformC)
                        {
                            // UPn from R239 bit 6-n as 0 or ~0 (UPT is ~0); a negation flips the table.
                            if ((c & 7) == 7)
                            {
                                Emit(0x0100000000000000UL | predicate | 252 | (15UL << 12) | ((ulong)~0U << 20));
                            }
                            else
                            {
                                Emit(0x3800000000000000UL | predicate | 252 | (239UL << 8) |
                                     ((ulong)((6 - (c & 7)) | (1U << 8)) << 20));
                                Emit(0x5c10000000000000UL | predicate | 252 | (255UL << 8) |
                                     (252UL << 20) | (1UL << 48));
                            }

                            if ((c & 8) != 0)
                            {
                                lut = ((lut & 0x55) << 1) | ((lut & 0xaa) >> 1);
                            }

                            continue;
                        }

                        Emit(0x38a0000000000000UL | predicate | (250 + i) | (255UL << 8) |
                             (0x7ffffUL << 20) | (1UL << 56) | ((ulong)(inputs[i] ^ 8U) << 39));
                    }

                    Emit(0x5be0000000000000UL | predicate | 255 | (250UL << 8) | (251UL << 20) |
                         ((ulong)lut << 28) | (3UL << 36) | (252UL << 39) | ((ulong)target << 48));
                }

                void MovImm(uint reg, uint value)
                {
                    Emit(0x0100000000000000UL | predicate | reg | (15UL << 12) | ((ulong)value << 20));
                }

                uint Materialize(uint kind, uint reg, uint scratch, bool absolute, bool negate)
                {
                    if (kind == 3)
                    {
                        uint mapped = UniformRegister(reg);

                        if (mapped == uint.MaxValue)
                        {
                            operandError = "native uniform source register";
                            return 255;
                        }

                        reg = mapped;
                    }
                    else if (kind == 1)
                    {
                        MovImm(scratch, insn.W1);
                        reg = scratch;
                    }
                    else if (kind == 2)
                    {
                        if (!ValidCbuf())
                        {
                            operandError = "native constant buffer address";
                            return 255;
                        }

                        Emit(0x4c98000000000000UL | predicate | scratch | (15UL << 39) | Cbuf());
                        reg = scratch;
                    }

                    if (absolute || negate)
                    {
                        if (reg != scratch)
                        {
                            Emit(0x5c98000000000000UL | predicate | scratch | ((ulong)reg << 20) | (15UL << 39));
                        }

                        if (absolute)
                        {
                            Emit(0x0400000000000000UL | predicate | scratch | ((ulong)scratch << 8) | (0x7fffffffUL << 20));
                        }

                        if (negate)
                        {
                            Emit(0x0400000000000000UL | predicate | scratch | ((ulong)scratch << 8) |
                                 (0x80000000UL << 20) | (2UL << 53));
                        }

                        reg = scratch;
                    }

                    return reg;
                }

                uint Operand(uint which, bool floating = true)
                {
                    uint form = insn.Bits(9, 3);
                    uint reg = 0, kind = 0, abs = 0, neg = 0;

                    if (which == 0)
                    {
                        reg = insn.Bits(24, 8);
                        abs = insn.Bits(73, 1);
                        neg = insn.Bits(72, 1);
                    }
                    else
                    {
                        if (form != 1 && form != 2 && form != 3 && form != 4 && form != 5 && form != 6 && form != 7)
                        {
                            operandError = "native ALU source form";
                            return 255;
                        }

                        bool wide = which == 1 ? (form == 4 || form == 5 || form == 6) : (form == 2 || form == 3 || form == 7);

                        if (wide)
                        {
                            kind = form == 6 || form == 7 ? 3U : (form == 4 || form == 2) ? 1U : 2U;

                            if (kind == 3)
                            {
                                reg = insn.Bits(32, 8);
                            }

                            if (kind == 2 || kind == 3)
                            {
                                abs = insn.Bits(62, 1);
                                neg = insn.Bits(63, 1);
                            }
                        }
                        else
                        {
                            bool upper = which == 1 ? (form == 2 || form == 3 || form == 7) : true;
                            reg = insn.Bits(upper ? 64 : 32, 8);
                            abs = insn.Bits(upper ? 74 : 62, 1);
                            neg = insn.Bits(upper ? 75 : 63, 1);
                        }
                    }

                    return Materialize(kind, reg, 250 + which, floating && abs != 0, floating && neg != 0);
                }

                HalfSource HalfSourceOf(uint which)
                {
                    if (which == 0)
                    {
                        return new HalfSource(insn.Bits(24, 8), insn.Bits(74, 2),
                            insn.Bits(73, 1) != 0, insn.Bits(72, 1) != 0);
                    }

                    uint form = insn.Bits(9, 3);
                    bool wide = which == 1 ? (form == 4 || form == 5) : (form == 2 || form == 3);

                    if (wide)
                    {
                        bool immediate = form == 2 || form == 4;
                        uint reg = Materialize(immediate ? 1U : 2U, 255, 250 + which, false, false);

                        return new HalfSource(reg, immediate ? 0U : insn.Bits(60, 2),
                            !immediate && insn.Bits(62, 1) != 0, !immediate && insn.Bits(63, 1) != 0);
                    }

                    bool upper = which == 2 || form == 2 || form == 3;

                    return new HalfSource(insn.Bits(upper ? 64 : 32, 8), insn.Bits(upper ? 81 : 60, 2),
                        insn.Bits(upper ? 83 : 62, 1) != 0, insn.Bits(upper ? 84 : 63, 1) != 0);
                }

                void ExtractHalf(HalfSource source, uint lane, uint target)
                {
                    bool high = source.Swizzle == 3 || (source.Swizzle == 0 && lane == 1);

                    Emit(0x5ca8000000000000UL | predicate | target | (2UL << 8) |
                         ((ulong)(source.Swizzle == 1 ? 2U : 1U) << 10) | ((ulong)source.Reg << 20) |
                         ((ulong)(high ? 1U : 0U) << 41) | ((ulong)(source.Negate ? 1U : 0U) << 45) |
                         ((ulong)(source.Absolute ? 1U : 0U) << 49));
                }

                void Xmad(uint target, uint a, uint b, uint c, bool aHigh, bool bHigh, uint mode, bool immediate)
                {
                    Emit((immediate ? 0x3600000000000000UL : 0x5b00000000000000UL) |
                         predicate | target | ((ulong)a << 8) |
                         ((ulong)(immediate ? (b >> (int)(bHigh ? 16 : 0)) & 0xffff : b) << 20) |
                         ((ulong)(!immediate && bHigh ? 1U : 0U) << 35) | ((ulong)c << 39) |
                         ((ulong)mode << 50) | ((ulong)(aHigh ? 1U : 0U) << 53));
                }

                void MultiplyLow(uint a, uint b, uint c, bool immediate)
                {
                    Xmad(250, a, b, c, false, false, 0, immediate);
                    Xmad(251, a, b, 255, true, false, 0, immediate);
                    Xmad(251, a, b, 251, false, true, 0, immediate);
                    Emit(Base(0x5c18000000000000UL) | (251UL << 8) | (250UL << 20) | (16UL << 39));
                }

                switch (opcode)
                {
                    case 0x304: // F2F.F16.F32, scalar round-to-nearest conversion.
                    {
                        if (insn.W2 != 0x00200800 || insn.Bits(24, 8) != 0 || (insn.W1 & ~0xc00000ffU) != 0)
                        {
                            return Fail(index, "native scalar float conversion mode");
                        }

                        Emit(Base(0x5ca8000000000000UL) | (1UL << 8) | (2UL << 10) |
                             ((ulong)insn.Bits(32, 8) << 20) | ((ulong)insn.Bits(62, 1) << 49) |
                             ((ulong)insn.Bits(63, 1) << 45));
                        break;
                    }
                    case 0x23e:
                    case 0xa3e: // F2FP.PACK_AB / MERGE_C, round-to-nearest
                    {
                        if (insn.Bits(62, 2) != 0 || insn.Bits(73, 5) != 0 || insn.Bits(79, 2) != 0 ||
                            insn.Bits(81, 10) != 0 || insn.Bits(91, 1) != 0)
                        {
                            return Fail(index, "native float packing modifiers");
                        }

                        bool merge = insn.Bits(78, 1) != 0;

                        if (insn.Bits(72, 1) != 0)
                        {
                            return Fail(index, "native float packing selector");
                        }

                        uint a = merge ? Operand(2, false) : Operand(0, false);
                        uint b = Operand(1, false);

                        // Canonical F2F writes the converted half in its low 16 bits.
                        if (merge)
                        {
                            Emit(0x5c98000000000000UL | predicate | 250 | ((ulong)a << 20) | (15UL << 39));
                        }
                        else
                        {
                            Emit(0x5ca8000000000000UL | predicate | 250 | (1UL << 8) |
                                 (2UL << 10) | ((ulong)a << 20));
                        }

                        Emit(0x5ca8000000000000UL | predicate | 251 | (1UL << 8) |
                             (2UL << 10) | ((ulong)b << 20));
                        Emit(Base(0x5c18000000000000UL) | (250UL << 8) | (251UL << 20) | (16UL << 39));
                        break;
                    }
                    case 0x232:
                    case 0x832:
                    case 0xa32: // HMUL2
                    {
                        if (insn.Bits(78, 2) != 0 || insn.Bits(81, 10) != 0 || insn.Bits(91, 1) != 0)
                        {
                            return Fail(index, "native half multiply mode");
                        }

                        HalfSource a = HalfSourceOf(0);
                        HalfSource b = HalfSourceOf(1);
                        uint precision = insn.Bits(76, 1) != 0 ? 2U : insn.Bits(80, 1);

                        Emit(Base(0x5d08000000000000UL) | ((ulong)a.Reg << 8) | ((ulong)b.Reg << 20) |
                             ((ulong)b.Swizzle << 28) | ((ulong)(b.Absolute ? 1U : 0U) << 30) |
                             ((ulong)(a.Negate != b.Negate ? 1U : 0U) << 31) |
                             ((ulong)insn.Bits(77, 1) << 32) | ((ulong)precision << 39) |
                             ((ulong)(a.Absolute ? 1U : 0U) << 44) | ((ulong)a.Swizzle << 47));
                        break;
                    }
                    case 0x230:
                    case 0x430:
                    case 0x630: // HADD2
                    {
                        if (insn.Bits(76, 1) != 0 || insn.Bits(79, 1) != 0 || insn.Bits(81, 10) != 0 ||
                            insn.Bits(91, 1) != 0)
                        {
                            return Fail(index, "native half add mode");
                        }

                        HalfSource a = HalfSourceOf(0);
                        HalfSource b = HalfSourceOf(opcode == 0x230 ? 1U : 2U);

                        if (insn.Bits(78, 1) != 0)
                        {
                            if (a.Reg != 255 || !a.Negate || a.Absolute || a.Swizzle != 0 ||
                                b.Swizzle < 2 || insn.Bits(77, 1) != 0 || insn.Bits(80, 1) != 0)
                            {
                                return Fail(index, "native float-output half add mode");
                            }

                            ExtractHalf(b, 0, dest);
                        }
                        else
                        {
                            Emit(Base(0x5d10000000000000UL) | ((ulong)a.Reg << 8) | ((ulong)b.Reg << 20) |
                                 ((ulong)b.Swizzle << 28) | ((ulong)(b.Absolute ? 1U : 0U) << 30) |
                                 ((ulong)(b.Negate ? 1U : 0U) << 31) |
                                 ((ulong)insn.Bits(77, 1) << 32) | ((ulong)insn.Bits(80, 1) << 39) |
                                 ((ulong)(a.Negate ? 1U : 0U) << 43) | ((ulong)(a.Absolute ? 1U : 0U) << 44) |
                                 ((ulong)a.Swizzle << 47));
                        }

                        break;
                    }
                    case 0x231:
                    case 0x431:
                    case 0x631:
                    case 0x831:
                    case 0xa31: // HFMA2
                    {
                        bool relu = insn.Bits(79, 1) != 0;

                        if (insn.Bits(78, 1) != 0 || insn.Bits(85, 1) != 0 || insn.Bits(91, 1) != 0 ||
                            (relu ? (insn.Bits(87, 4) != 7 || insn.Bits(77, 1) != 0) : insn.Bits(87, 4) != 0))
                        {
                            return Fail(index, "native half fused multiply-add mode");
                        }

                        if (relu && nativePredicate != 7)
                        {
                            // Guard the whole expansion before its temporary P6 updates.
                            relocations.Add(new Relocation((uint)result.Instructions.Count, index + 1));
                            Emit(0xe24000000000000fUL | ((ulong)(nativePredicate ^ 8U) << 16));
                            predicate = 7UL << 16;
                        }

                        HalfSource a = HalfSourceOf(0);
                        HalfSource b = HalfSourceOf(1);
                        HalfSource c = HalfSourceOf(2);

                        if (insn.Bits(86, 1) != 0) // H0_NH1: negate only the upper packed component.
                        {
                            if (b.Swizzle != 0 || b.Absolute || b.Reg >= 250)
                            {
                                return Fail(index, "native half mixed-sign source mode");
                            }

                            Emit(0x0400000000000000UL | predicate | 251 | ((ulong)b.Reg << 8) |
                                 (0x80000000UL << 20) | (2UL << 53));
                            b = new HalfSource(251, b.Swizzle, b.Absolute, b.Negate);
                        }

                        if (b.Absolute || c.Absolute || (a.Absolute && a.Reg >= 248))
                        {
                            return Fail(index, "native half fused multiply-add absolute source");
                        }

                        if (a.Absolute)
                        {
                            Emit(0x0400000000000000UL | predicate | 250 | ((ulong)a.Reg << 8) |
                                 ((ulong)(a.Swizzle == 1 ? 0x7fffffffU : 0x7fff7fffU) << 20));
                            a = new HalfSource(250, a.Swizzle, false, a.Negate);
                        }

                        uint precision2 = insn.Bits(76, 1) != 0 ? 2U : insn.Bits(80, 1);

                        Emit(0x5d00000000000000UL | predicate | (relu ? 254UL : dest) | ((ulong)a.Reg << 8) |
                             ((ulong)b.Reg << 20) | ((ulong)b.Swizzle << 28) |
                             ((ulong)(c.Negate ? 1U : 0U) << 30) |
                             ((ulong)(a.Negate != b.Negate ? 1U : 0U) << 31) |
                             ((ulong)insn.Bits(77, 1) << 32) | ((ulong)c.Swizzle << 35) |
                             ((ulong)precision2 << 37) | ((ulong)c.Reg << 39) | ((ulong)a.Swizzle << 47));

                        if (relu)
                        {
                            // Clamp the rounded binary16 lanes, preserving positive infinity
                            // and canonicalizing either sign of NaN without saturating to one.
                            SavePredicate();

                            for (uint lane = 0; lane < 2; ++lane)
                            {
                                Emit(0x3828000000070000UL | 251 | (254UL << 8) | (lane * 16UL << 20));
                                Emit(0x0400000000070000UL | 251 | (251UL << 8) | (0xffffUL << 20));
                                Emit(0x0400000000070000UL | 252 | (251UL << 8) | (0x7fffUL << 20));
                                MovImm(250, 0x7c00);
                                Emit(0x5b60000000070000UL | 7 | (6UL << 3) | (250UL << 8) |
                                     (252UL << 20) | (7UL << 39) | (1UL << 49));
                                Emit(0x3828000000070000UL | 252 | (251UL << 8) | (15UL << 20));
                                MovImm(250, 1);
                                Emit(0x5c10000000070000UL | 252 | (252UL << 8) | (250UL << 20) | (1UL << 48));
                                Emit(0x5c40000000070000UL | 251 | (251UL << 8) | (252UL << 20) | (7UL << 48));
                                MovImm(250, 0x7fff);
                                Emit(0x5ca0000000070000UL | 251 | (250UL << 8) | (251UL << 20) | (6UL << 39));

                                if (lane == 0)
                                {
                                    Emit(Base(0x5c98000000000000UL) | (251UL << 20) | (15UL << 39));
                                }
                                else
                                {
                                    Emit(0x3848000000070000UL | 251 | (251UL << 8) | (16UL << 20));
                                    Emit(Base(0x5c40000000000000UL) | (dest << 8) | (251UL << 20) |
                                         (1UL << 41) | (7UL << 48));
                                }
                            }

                            RestorePredicate();
                        }

                        break;
                    }
                    case 0x240:
                    case 0x840: // HMNMX2, exact half promotion for min/max
                    {
                        if (insn.Bits(76, 11) != 0 || insn.Bits(91, 1) != 0)
                        {
                            return Fail(index, "native half min/max mode");
                        }

                        HalfSource a = HalfSourceOf(0);
                        HalfSource b = opcode == 0x840 ? new HalfSource(255, 0, false, false) : HalfSourceOf(1);

                        if (a.Swizzle == 1 || b.Swizzle == 1 || (b.Reg >= 250 && b.Reg != 255))
                        {
                            return Fail(index, "native half min/max source mode");
                        }

                        for (uint lane = 0; lane < 2; ++lane)
                        {
                            ExtractHalf(a, lane, 250);

                            if (opcode == 0x840)
                            {
                                uint half = (insn.W1 >> (int)(lane * 16)) & 0xffff;
                                MovImm(251, half);
                                ExtractHalf(new HalfSource(251, 2, false, false), 0, 251);
                            }
                            else
                            {
                                ExtractHalf(b, lane, 251);
                            }

                            uint target = lane == 0 ? 252U : 250U;

                            Emit(0x5c60000000000000UL | predicate | target | (250UL << 8) | (251UL << 20) |
                                 ((ulong)insn.Bits(87, 3) << 39) | ((ulong)insn.Bits(90, 1) << 42));
                            Emit(0x5ca8000000000000UL | predicate | target | (1UL << 8) |
                                 (2UL << 10) | ((ulong)target << 20));
                        }

                        Emit(Base(0x5c18000000000000UL) | (250UL << 8) | (252UL << 20) | (16UL << 39));
                        break;
                    }
                    case 0xc24:
                    case 0xe24: // IMAD low word with a uniform multiplier or addend.
                    {
                        // 0xe24 keeps the uniform addend in bits 32-37; bit 63 negates it ("RZ, RZ, -UR4").
                        bool negateC = opcode == 0xe24 && insn.Bits(63, 1) != 0;

                        if ((insn.W2 & ~0x080002ffU) != 0x078e0000 ||
                            (insn.W1 & ~(0xffU | (negateC ? 0x80000000U : 0U))) != 0)
                        {
                            return Fail(index, "native uniform-addend multiply mode");
                        }

                        uint a = Operand(0, false), b = Operand(1, false);
                        uint c = Operand(2, false);

                        if (negateC)
                        {
                            Emit(0x5c10000000000000UL | predicate | 252 | (255UL << 8) | ((ulong)c << 20) |
                                 (1UL << 48));
                            c = 252;
                        }

                        MultiplyLow(a, b, c, false);
                        break;
                    }
                    case 0x224: // IMAD register / zero-product move
                    {
                        if (insn.Bits(24, 8) == 255 && insn.W1 == 255 && insn.Bits(75, 1) != 0 &&
                            (insn.W2 & ~0xaffU) == 0x078e0000)
                        {
                            Emit(Base(0x5c10000000000000UL) | (255UL << 8) |
                                 ((ulong)insn.Bits(64, 8) << 20) | (1UL << 48));
                            break;
                        }

                        if (insn.Bits(24, 8) == 255 && insn.W1 == 255 && (insn.W2 & ~0xffU) == 0x078e0000)
                        {
                            Emit(Base(0x5c98000000000000UL) | (15UL << 39) | ((ulong)insn.Bits(64, 8) << 20));
                            break;
                        }

                        if ((insn.W2 & ~0x2ffU) != 0x078e0000 || (insn.W1 & ~0xffU) != 0)
                        {
                            return Fail(index, "native integer register multiply mode");
                        }

                        MultiplyLow(insn.Bits(24, 8), insn.Bits(32, 8), insn.Bits(64, 8), false);
                        break;
                    }
                    case 0x2a5: // UIMAD.WIDE.U32 UR(d):UR(d+1) = URa * URb + URc:UR(c+1)
                    {
                        uint a = UniformRegister(insn.Bits(24, 8)), b = UniformRegister(insn.Bits(32, 8));
                        uint c = insn.Bits(64, 8);
                        uint cLow = UniformRegister(c), cHigh = c == 63 ? 255U : UniformRegister(c + 1);
                        uint dLow = UniformRegister(insn.Bits(16, 8)), dHigh = UniformRegister(insn.Bits(16, 8) + 1);

                        if (nativePredicate != 7 || (insn.W1 & ~0xffU) != 0 ||
                            (insn.W2 & ~0xffU) != 0x0f8e0000 || (insn.Bits(16, 8) & 1) != 0 ||
                            (c != 63 && (c & 1) != 0) || a == uint.MaxValue || b == uint.MaxValue ||
                            cLow == uint.MaxValue || cHigh == uint.MaxValue || dLow == uint.MaxValue ||
                            dHigh == uint.MaxValue || dLow == 255)
                        {
                            return Fail(index, "native uniform wide multiply mode");
                        }

                        // High word in R253 (base-2^16 long multiplication, as IMAD.HI), low word in R250.
                        Xmad(250, a, b, 255, false, false, 0, false);
                        Xmad(251, a, b, 250, true, false, 2, false);
                        Xmad(250, a, b, 251, false, true, 1, false);
                        Xmad(252, a, b, 251, true, true, 2, false);
                        Emit(0x3828000000070000UL | 250 | (250UL << 8) | (16UL << 20));
                        Emit(0x5c10000000070000UL | 253 | (250UL << 8) | (252UL << 20));
                        Xmad(250, a, b, 255, false, false, 0, false);
                        Xmad(251, a, b, 255, true, false, 0, false);
                        Xmad(251, a, b, 251, false, true, 0, false);
                        Emit(0x5c18000000070000UL | 250 | (251UL << 8) | (250UL << 20) | (16UL << 39));
                        Emit(0x5c10000000070000UL | 250 | (250UL << 8) | ((ulong)cLow << 20) | (1UL << 47));
                        Emit(0x5c10000000070000UL | dHigh | (253UL << 8) | ((ulong)cHigh << 20) | (1UL << 43));
                        Emit(0x5c10000000070000UL | dLow | (250UL << 8) | (255UL << 20));
                        break;
                    }
                    case 0x827: // IMAD.HI.U32, captured zero addend
                    {
                        if (insn.W2 != 0x078e00ff)
                        {
                            return Fail(index, "native high multiply mode");
                        }

                        uint a = insn.Bits(24, 8), b = insn.W1;

                        // Base-2^16 long multiplication. Each intermediate sum fits U32.
                        Xmad(250, a, b, 255, false, false, 0, true);
                        Xmad(251, a, b, 250, true, false, 2, true);
                        Xmad(250, a, b, 251, false, true, 1, true);
                        Xmad(252, a, b, 251, true, true, 2, true);
                        Emit(0x3828000000000000UL | predicate | 250 | (250UL << 8) | (16UL << 20));
                        Emit(Base(0x5c10000000000000UL) | (250UL << 8) | (252UL << 20));
                        break;
                    }
                    case 0xa24: // IMAD, constant-buffer multiplier
                    {
                        if ((insn.W2 & ~0x2ffU) != 0x078e0000 || !ValidCbuf() || insn.Bits(62, 2) != 0)
                        {
                            return Fail(index, "native integer constant-buffer multiply mode");
                        }

                        Emit(0x4c98000000000000UL | predicate | 252 | (15UL << 39) | Cbuf());
                        MultiplyLow(insn.Bits(24, 8), 252, insn.Bits(64, 8), false);
                        break;
                    }
                    case 0x424:
                    case 0x624: // IMAD.MOV.U32: zero product plus immediate/cbuf
                    {
                        if (opcode == 0x624 && insn.Bits(24, 8) == 255 &&
                            (insn.W2 & ~0x07800000U) == 0x000e06ffU)
                        {
                            uint source = Operand(2, false);
                            MovImm(250, 1);
                            Emit(0x5ca0000000000000UL | predicate | 250 | (250UL << 8) | (255UL << 20) |
                                 ((ulong)insn.Bits(87, 3) << 39) | ((ulong)insn.Bits(90, 1) << 42));
                            Emit(Base(0x5c10000000000000UL) | ((ulong)source << 8) | (250UL << 20));
                            break;
                        }

                        if (insn.Bits(24, 8) != 255 || insn.Bits(64, 8) != 255 || insn.W2 != 0x078e00ff)
                        {
                            if ((insn.W2 & ~0x2ffU) != 0x078e0000 || (opcode == 0x624 && insn.Bits(62, 2) != 0))
                            {
                                return Fail(index, "native multiply wide addend mode");
                            }

                            uint a = Operand(0, false), b = Operand(1, false), c = Operand(2, false);
                            MultiplyLow(a, b, c, false);
                            break;
                        }

                        if (opcode == 0x624)
                        {
                            if (!ValidCbuf() || insn.Bits(62, 2) != 0)
                            {
                                return Fail(index, "native integer move constant buffer");
                            }

                            Emit(Base(0x4c98000000000000UL) | (15UL << 39) | Cbuf());
                        }
                        else
                        {
                            Emit(Base(0x0100000000000000UL) | (15UL << 12) | ((ulong)insn.W1 << 20));
                        }

                        break;
                    }
                    case 0x824: // Captured low-word IMAD forms; bit 75 negates the addend.
                    {
                        bool negateC = insn.Bits(75, 1) != 0;

                        if (insn.W1 == 1 && (insn.W2 & 0xfffff700U) == 0x078e0200)
                        {
                            Emit(Base(0x5c10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)insn.Bits(64, 8) << 20) | ((ulong)(negateC ? 1U : 0U) << 48));
                            break;
                        }

                        uint multiplier = insn.W1;

                        if (insn.Bits(64, 8) == 255 && !negateC && multiplier > 1 &&
                            (multiplier & (multiplier - 1)) == 0 && (insn.W2 & ~0x2ffU) == 0x078e0000)
                        {
                            // IMAD.SHL by a power of two: one shift (also the form Eden's jump-table tracking
                            // expects between the clamp and the table load).
                            uint shift = 0;

                            while ((1U << (int)shift) != multiplier)
                            {
                                ++shift;
                            }

                            Emit(Base(0x3848000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) | ((ulong)shift << 20));
                            break;
                        }

                        if (insn.W1 != 48 || insn.W2 != 0x078e02ff)
                        {
                            if ((insn.W2 & ~0xaffU) != 0x078e0000)
                            {
                                return Fail(index, "native integer multiply mode");
                            }

                            uint c = insn.Bits(64, 8);

                            if (negateC && c != 255)
                            {
                                Emit(0x5c10000000000000UL | predicate | 252 | (255UL << 8) | ((ulong)c << 20) |
                                     (1UL << 48));
                                c = 252;
                            }

                            MultiplyLow(insn.Bits(24, 8), insn.W1, c, true);
                            break;
                        }

                        ulong source = insn.Bits(24, 8);
                        Emit(0x5c18000000000000UL | predicate | 250 | (source << 8) | (source << 20) | (1UL << 39));
                        Emit(Base(0x3848000000000000UL) | (250UL << 8) | (4UL << 20));
                        break;
                    }
                    case 0x806: // VOTE: register ballot, ordinary predicate input and output.
                    {
                        if (insn.W1 != 0 || insn.Bits(72, 2) > 2 || (insn.W2 & ~0x078e0300U) != 0)
                        {
                            return Fail(index, "native vote mode");
                        }

                        Emit(Base(0x50d8000000000000UL) | ((ulong)insn.Bits(87, 4) << 39) |
                             ((ulong)insn.Bits(81, 3) << 45) | ((ulong)insn.Bits(72, 2) << 48));
                        break;
                    }
                    case 0x886: // VOTEU ballot and independent uniform predicate output.
                    {
                        uint output = insn.Bits(81, 3), target = insn.Bits(16, 8);

                        if ((target >= 16 && target != 63) || insn.W1 != 0 ||
                            insn.Bits(72, 2) > 2 || nativePredicate != 7)
                        {
                            return Fail(index, "native uniform vote mode");
                        }

                        uint temporary = output == 7 ? 7U : 6 - output;
                        uint mask = 1U << (int)temporary;

                        if (output != 7)
                        {
                            Emit(0x38e8000000070000UL | 253 | (255UL << 8) | ((ulong)mask << 20));
                        }

                        Emit(0x50d8000000070000UL | UniformRegister(target) |
                             ((ulong)insn.Bits(87, 4) << 39) | ((ulong)temporary << 45) |
                             ((ulong)insn.Bits(72, 2) << 48));

                        if (output != 7)
                        {
                            Emit(0x38e8000000070000UL | 239 | (239UL << 8) | ((ulong)mask << 20));
                            Emit(0x38f0000000070000UL | (253UL << 8) | ((ulong)mask << 20));
                        }

                        break;
                    }
                    case 0x3c2: // R2UR: broadcast the first lane satisfying the execution predicate.
                    {
                        uint target = UniformRegister(dest);

                        if (target == uint.MaxValue || target == 255 || insn.W1 != 0 || insn.W2 != 0x000e0000)
                        {
                            return Fail(index, "native register-to-uniform mode");
                        }

                        SavePredicate();
                        Emit(0x50d8000000070000UL | 250 | ((ulong)nativePredicate << 39) |
                             (6UL << 45) | (1UL << 48));
                        Emit(0x5c10000000070000UL | 251 | (250UL << 8) | (255UL << 20) | (1UL << 49));
                        Emit(0x5c40000000070000UL | 250 | (250UL << 8) | (251UL << 20) | (7UL << 48));
                        Emit(0x5c30000000070000UL | 250 | (250UL << 20));
                        Emit(0xef10000000070000UL | 251 | ((ulong)insn.Bits(24, 8) << 8) |
                             (250UL << 20) | (1UL << 29) | (31UL << 34) | (7UL << 48));
                        Emit(0x5ca0000000070000UL | target | (251UL << 8) |
                             ((ulong)target << 20) | (6UL << 39));
                        RestorePredicate();
                        break;
                    }
                    case 0x81c: // PLOP3.LUT; bit 67 makes the third source a uniform predicate.
                    {
                        uint target = insn.Bits(81, 3);
                        uint lut = insn.Bits(64, 3) | (insn.Bits(72, 5) << 3);

                        if (target == 7 || insn.Bits(84, 3) != 7 || insn.Bits(16, 8) != 0 || insn.W1 != 0)
                        {
                            return Fail(index, "native predicate LUT mode");
                        }

                        if (insn.Bits(67, 1) == 0 && (lut == 0xfe || lut == 0xa8))
                        {
                            Emit(0x5090000000000000UL | predicate | ((ulong)target << 3) | 7 |
                                 ((ulong)insn.Bits(87, 4) << 12) | (1UL << 24) |
                                 ((ulong)insn.Bits(77, 4) << 29) | ((ulong)insn.Bits(68, 4) << 39) |
                                 ((ulong)(lut == 0xfe ? 1U : 0U) << 45));
                            break;
                        }

                        if (insn.Bits(67, 1) == 0 && (lut == 0x80 ||
                            (lut == 8 && insn.Bits(68, 4) == 7 && insn.Bits(77, 4) == 7)))
                        {
                            Emit(0x5090000000000000UL | predicate | ((ulong)target << 3) | 7 |
                                 ((ulong)(insn.Bits(87, 4) ^ (lut == 8 ? 8U : 0U)) << 12) |
                                 ((ulong)insn.Bits(77, 4) << 29) | ((ulong)insn.Bits(68, 4) << 39));
                            break;
                        }

                        PredicateLut(lut, insn.Bits(87, 4), insn.Bits(77, 4), insn.Bits(68, 4), target,
                            insn.Bits(67, 1) != 0);
                        break;
                    }
                    case 0x89c: // UPLOP3.LUT: load the uniform predicates into P0-P6, combine, merge back.
                    {
                        uint target = insn.Bits(81, 3);
                        uint lut = insn.Bits(64, 3) | (insn.Bits(72, 5) << 3);

                        if (insn.Bits(84, 3) != 7 || insn.Bits(67, 1) != 0 || insn.Bits(16, 8) != 0 || insn.W1 != 0)
                        {
                            return Fail(index, "native uniform predicate LUT mode");
                        }

                        if (target == 7)
                        {
                            break;
                        }

                        static uint Slot(uint operand) => (operand & 7) == 7 ? operand : (operand & 8) | (6 - (operand & 7));

                        predicate = (ulong)Slot(nativePredicate) << 16; // a UPn guard is loaded as well
                        Emit(0x38e8000000070000UL | 254 | (255UL << 8) | (0x7fUL << 20));
                        Emit(0x38f0000000070000UL | (239UL << 8) | (0x7fUL << 20));
                        PredicateLut(lut, Slot(insn.Bits(87, 4)), Slot(insn.Bits(77, 4)), Slot(insn.Bits(68, 4)),
                            6 - target);
                        Emit(0x38e8000000070000UL | 239 | (239UL << 8) | ((ulong)(1U << (int)(6 - target)) << 20));
                        Emit(0x38f0000000070000UL | (254UL << 8) | (0x7fUL << 20));
                        break;
                    }
                    case 0x883: // UP2UR UR, UPR, URZ, mask: bit k of the result is UPk (R239 bit 6-k).
                    {
                        uint target = UniformRegister(insn.Bits(16, 8)), mask = insn.W1;

                        if (target == uint.MaxValue || insn.Bits(24, 8) != 63 || nativePredicate != 7 ||
                            insn.W2 != 0x08000000 || mask > 0x7f)
                        {
                            return Fail(index, "native uniform predicate move mode");
                        }

                        if (target == 255)
                        {
                            break;
                        }

                        MovImm(target, 0);

                        for (uint k = 0; k < 7; ++k)
                        {
                            if (((mask >> (int)k) & 1) == 0)
                            {
                                continue;
                            }

                            Emit(0x3800000000070000UL | 250 | (239UL << 8) |
                                 ((ulong)((6 - k) | (1U << 8)) << 20));
                            Emit(0x36f0000000070000UL | target | (250UL << 8) |
                                 ((ulong)(k | (1U << 8)) << 20) | ((ulong)target << 39));
                        }

                        break;
                    }
                    case 0x882: // UMOV immediate.
                    {
                        if (dest >= 16 || insn.W2 != 0)
                        {
                            return Fail(index, "native uniform immediate move mode");
                        }

                        MovImm(dest + 216, insn.W1);
                        break;
                    }
                    case 0xc82: // UMOV UR, UR (or URZ), including uniform execution predicates.
                    {
                        uint source = insn.W1 < 64 ? UniformRegister(insn.W1) : uint.MaxValue;

                        if (dest >= 16 || source == uint.MaxValue || insn.W2 != 0x08000000)
                        {
                            return Fail(index, "native uniform move mode");
                        }

                        Emit(0x5c98000000000000UL | predicate | (dest + 216) |
                             ((ulong)source << 20) | (15UL << 39));
                        break;
                    }
                    case 0xab9:
                    case 0xabb: // ULDC U32/U64, direct or indexed.
                    {
                        uint uniformDest = insn.Bits(16, 8), indexReg = insn.Bits(24, 8);
                        uint width = insn.Bits(73, 3);
                        uint target = UniformRegister(uniformDest);
                        uint indexTarget = opcode == 0xabb ? UniformRegister(indexReg) : 255U;

                        if (target == uint.MaxValue || target == 255 || (width != 4 && width != 5) ||
                            (width == 5 && ((uniformDest & 1) != 0 ||
                                            UniformRegister(uniformDest + 1) != target + 1)) ||
                            Bank() >= 18 || insn.Bits(38, 16) % 4 != 0 || insn.Bits(62, 2) != 0 ||
                            (insn.W2 & ~0xe00U) != (opcode == 0xab9 ? 0U : 0x08000000U) ||
                            indexTarget == uint.MaxValue)
                        {
                            return Fail(index, "native uniform constant load mode");
                        }

                        if (opcode == 0xab9 && width == 4)
                        {
                            Emit(0x4c98000000000000UL | predicate | target | (15UL << 39) | Cbuf());
                        }
                        else
                        {
                            Emit(0xef90000000000000UL | predicate | target | ((ulong)indexTarget << 8) |
                                 ((ulong)insn.Bits(38, 16) << 20) | ((ulong)Bank() << 36) |
                                 ((ulong)width << 48));
                        }

                        break;
                    }
                    case 0x899: // USHF.L.U32; accept a zero word outside the selected half.
                    {
                        uint dst = insn.Bits(16, 8), low = insn.Bits(24, 8), high = insn.Bits(64, 8);

                        if (insn.W2 == (0x08011400U | high) && low == 63 && high < 16 &&
                            dst < 16 && nativePredicate == 7 && insn.W1 <= 32)
                        {
                            Emit(0x3828000000000000UL | predicate | (dst + 216) |
                                 ((ulong)(high + 216) << 8) | ((ulong)insn.W1 << 20) | (1UL << 48));
                            break;
                        }

                        // Logical right shifts of one register (bit 76 right, bit 80 high word, bits 73-74
                        // U32): USHF.R.U32 URd, URa, n, URZ and USHF.R.U32.HI URd, URZ, n, URb.
                        bool rightLow = insn.W2 == 0x0800163f && low < 16;
                        bool rightHigh = insn.W2 == (0x08011600U | high) && low == 63 && high < 16;

                        if ((rightLow || rightHigh) && dst < 16 && nativePredicate == 7 && insn.W1 <= 32)
                        {
                            Emit(0x3828000000000000UL | predicate | (dst + 216) |
                                 ((ulong)((rightHigh ? high : low) + 216) << 8) | ((ulong)insn.W1 << 20));
                            break;
                        }

                        bool highResult = insn.W2 == (0x08010600U | high) && low == 63 && high < 16;
                        bool lowResult = insn.W2 == 0x0800063f && low < 16;

                        if (dst >= 16 || nativePredicate != 7 || insn.W1 > 32 || (!highResult && !lowResult))
                        {
                            return Fail(index, "native uniform shift mode");
                        }

                        Emit(0x3848000000000000UL | predicate | (dst + 216) |
                             ((ulong)((highResult ? high : low) + 216) << 8) | ((ulong)insn.W1 << 20));
                        break;
                    }
                    case 0xb82: // LDC.64 indexed byte offset
                    {
                        uint width = insn.Bits(73, 3);

                        if ((width != 4 && width != 5) || insn.Bits(78, 2) != 0 || insn.Bits(91, 1) != 0 ||
                            Bank() >= 18 || (width == 5 && ((dest & 1) != 0 || dest >= 254)))
                        {
                            return Fail(index, "native indexed constant load mode");
                        }

                        Emit(Base(0xef90000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)insn.Bits(38, 16) << 20) | ((ulong)Bank() << 36) | ((ulong)width << 48));
                        break;
                    }
                    case 0xf89: // SHFL with immediate lane and segmentation/clamp mask, any mode.
                    {
                        uint lane = insn.Bits(53, 5), mask = insn.Bits(40, 13), mode = insn.Bits(58, 2);

                        if (insn.W2 != 0x000e0000 || insn.Bits(32, 8) != 0 || insn.Bits(60, 4) != 0)
                        {
                            return Fail(index, "native immediate shuffle mode");
                        }

                        Emit(Base(0xef10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)lane << 20) | (1UL << 28) | (1UL << 29) |
                             ((ulong)mode << 30) | ((ulong)mask << 34) | (7UL << 48));
                        break;
                    }
                    case 0x822: // FSWZADD: per lane 0 = a+b, 1 = a-b, 2 = b-a, 3 = b (nvdisasm PP/PN/NP/ZP).
                    {
                        uint swizzle = insn.W1;

                        if (swizzle > 0xff || insn.W2 > 255)
                        {
                            return Fail(index, "native swizzled add mode");
                        }

                        // Maxwell's lane codes 1 and 2 are the other way round (1 = b-a, 2 = a-b).
                        uint maxwell = ((swizzle & 0x55) << 1) | ((swizzle & 0xaa) >> 1);

                        Emit(Base(0x50f8000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)insn.Bits(64, 8) << 20) | ((ulong)maxwell << 28));
                        break;
                    }
                    case 0xb6c: // TXD 2D or ARRAY_2D, four channels; the two destination pairs anywhere.
                    {
                        uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
                        uint coordinates = insn.Bits(24, 8), derivatives = insn.Bits(32, 8);

                        // Ampere dimensions: 1 = 2D, 5 = ARRAY_2D; Maxwell TXD's are 2 and 3. The coordinates
                        // (x, y[, layer]) and the four gradients sit in the same registers for both.
                        uint dimension = insn.Bits(61, 3);
                        uint high = insn.Bits(64, 8); // destination of the third and fourth channels

                        if ((insn.W2 & ~0xffU) != 0x001e0f00 || insn.Bits(59, 2) != 0 ||
                            (dimension != 1 && dimension != 5) || binding >= 18 || offset > 0x1fff ||
                            (dest & 1) != 0 || dest >= 248 || (high & 1) != 0 || (high >= 248 && high != 255) ||
                            coordinates >= 248 || derivatives >= 247)
                        {
                            return Fail(index, "native gradient texture mode");
                        }

                        if (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding)
                        {
                            return Fail(index, "native texture descriptor buffer conflict");
                        }

                        result.RequiredTextureBuffer = binding;

                        // Maxwell writes four consecutive registers: when the second pair is elsewhere (Splatoon
                        // Raiders: R12/R18, R18/R44), write them to scratch and move each pair into place.
                        bool consecutive = high == dest + 2;
                        ulong target = consecutive ? dest : 248;

                        Emit(0xde00000000000000UL | predicate | target | ((ulong)coordinates << 8) |
                             ((ulong)derivatives << 20) | ((ulong)(dimension == 5 ? 3U : 2U) << 28) |
                             (15UL << 31) | ((ulong)offset << 36) | ((ulong)insn.Bits(90, 1) << 49) |
                             (7UL << 51));

                        if (!consecutive)
                        {
                            for (uint i = 0; i < 2; ++i)
                            {
                                Emit(0x5c98000000000000UL | predicate | (dest + i) | (15UL << 39) |
                                     ((ulong)(248 + i) << 20));

                                if (high != 255)
                                {
                                    Emit(0x5c98000000000000UL | predicate | (high + i) | (15UL << 39) |
                                         ((ulong)(250 + i) << 20));
                                }
                            }
                        }

                        break;
                    }
                    case 0x989: // SHFL, immediate lane and register clamp/segmentation mask, any mode.
                    {
                        if ((insn.W1 & 0xf01fffffU) != 0 || (insn.W2 & ~0x000e00ffU) != 0)
                        {
                            return Fail(index, "native register-mask shuffle mode");
                        }

                        Emit(Base(0xef10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)insn.Bits(53, 5) << 20) | (1UL << 28) |
                             ((ulong)insn.Bits(58, 2) << 30) | ((ulong)insn.Bits(64, 8) << 39) |
                             ((ulong)insn.Bits(81, 3) << 48));
                        break;
                    }
                    case 0x589: // SHFL.IDX, register lane and immediate segmentation mask.
                    {
                        if ((insn.W2 & ~0x000e0000U) != 0 || insn.Bits(53, 11) != 0)
                        {
                            return Fail(index, "native shuffle mode");
                        }

                        Emit(0xef10000000000000UL | predicate | dest |
                             ((ulong)insn.Bits(24, 8) << 8) | ((ulong)insn.Bits(32, 8) << 20) |
                             (1UL << 29) | ((ulong)insn.Bits(40, 13) << 34) | ((ulong)insn.Bits(81, 3) << 48));
                        break;
                    }
                    case 0x98e: // RED.E.{ADD..XOR}.STRONG.GPU U32 (op in bits 87-89), register address.
                    {
                        if ((insn.W2 & ~0x0380003fU) == 0x0c10e100 && insn.Bits(24, 8) == 255)
                        {
                            // [URa + offset]: a uniform 64-bit address with a signed byte offset.
                            uint address = UniformRegister(insn.Bits(64, 6));
                            int offset = unchecked((int)(insn.Bits(40, 24) << 8)) >> 8;

                            if (address == uint.MaxValue || (address & 1) != 0 || offset < -0x80000 || offset > 0x7ffff)
                            {
                                return Fail(index, "native global reduction uniform address");
                            }

                            Emit(0xebf8000000000000UL | predicate | insn.Bits(32, 8) | ((ulong)address << 8) |
                                 ((ulong)insn.Bits(87, 3) << 23) | ((ulong)(uint)offset & 0xfffffUL) << 28 |
                                 (1UL << 48));
                            break;
                        }

                        if ((insn.W2 & ~0x03800000U) != 0x0010e100 || insn.Bits(40, 24) != 0 ||
                            (insn.Bits(24, 8) & 1) != 0)
                        {
                            return Fail(index, "native global reduction mode");
                        }

                        Emit(0xebf8000000000000UL | predicate | insn.Bits(32, 8) |
                             ((ulong)insn.Bits(24, 8) << 8) | ((ulong)insn.Bits(87, 3) << 23) | (1UL << 48));
                        break;
                    }
                    case 0x2bd: // UFLO.U32, the highest set-bit index (.SH, bit 74: its shift amount).
                    {
                        if (dest >= 16 || insn.W1 >= 16 || (insn.W2 & ~0x400U) != 0x080e0000 || nativePredicate != 7)
                        {
                            return Fail(index, "native uniform leading bit mode");
                        }

                        Emit(0x5c30000000000000UL | predicate | (dest + 216) |
                             ((ulong)(insn.W1 + 216) << 20) | ((ulong)insn.Bits(74, 1) << 41));
                        break;
                    }
                    case 0x2be: // UBREV, as a bit-reversing zero shift like BREV.
                    {
                        if (dest >= 16 || insn.W1 >= 16 || insn.W2 != 0x08000000 || nativePredicate != 7)
                        {
                            return Fail(index, "native uniform bit reverse mode");
                        }

                        Emit(0x3828000000000000UL | predicate | (dest + 216) |
                             ((ulong)(insn.W1 + 216) << 8) | (1UL << 39) | (1UL << 40));
                        break;
                    }
                    case 0x3a1: // MATCH.ALL.U64 with only the all-equal predicate retained.
                    {
                        uint source = insn.Bits(24, 8), target = insn.Bits(81, 3);

                        if (dest != 255 || source >= 254 || (source & 1) != 0 || target >= 6 ||
                            nativePredicate != 7 || insn.W1 != 0 || (insn.W2 & ~0xe0000U) != 0x200)
                        {
                            return Fail(index, "native warp match mode");
                        }

                        SavePredicate();
                        Emit(0x50d8000000070000UL | 250 | (7UL << 39) | (7UL << 45) | (1UL << 48));
                        Emit(0x5c10000000070000UL | 251 | (250UL << 8) | (255UL << 20) | (1UL << 49));
                        Emit(0x5c40000000070000UL | 250 | (250UL << 8) | (251UL << 20) | (7UL << 48));
                        Emit(0x5c30000000070000UL | 250 | (250UL << 20));

                        for (uint word = 0; word < 2; ++word)
                        {
                            Emit(0xef10000000070000UL | (251 + word) | ((ulong)(source + word) << 8) |
                                 (250UL << 20) | (1UL << 29) | (31UL << 34) | (7UL << 48));
                        }

                        Emit(0x5b60000000070000UL | 7 | (6UL << 3) | ((ulong)source << 8) |
                             (251UL << 20) | (7UL << 39) | (2UL << 49));
                        Emit(0x5b60000000070000UL | 7 | (6UL << 3) | ((ulong)(source + 1) << 8) |
                             (252UL << 20) | (6UL << 39) | (2UL << 49));
                        Emit(0x50d8000000070000UL | 255 | (6UL << 39) | ((ulong)target << 45));
                        RestorePredicate();
                        break;
                    }
                    case 0x3c4: // REDUX.SUM (modulo 2^32) or .MAX (unsigned) over the actual active lanes.
                    {
                        uint source = insn.Bits(24, 8), target = UniformRegister(dest);
                        bool maximum = insn.W2 == 0x14000;

                        if (target == uint.MaxValue || target == 255 || insn.W1 != 0 ||
                            (insn.W2 != 0xc000 && !maximum) || nativePredicate != 7)
                        {
                            return Fail(index, "native warp sum mode");
                        }

                        Emit(0x50d8000000070000UL | 250 | (7UL << 39) | (7UL << 45) | (1UL << 48));
                        MovImm(target, 0);

                        for (uint lane = 0; lane < 32; ++lane)
                        {
                            Emit(0xef10000000070000UL | 251 | ((ulong)source << 8) | ((ulong)lane << 20) |
                                 (1UL << 28) | (1UL << 29) | (31UL << 34) | (7UL << 48));
                            Emit(0x3828000000070000UL | 252 | (250UL << 8) | ((ulong)lane << 20));
                            Emit(0x0400000000070000UL | 252 | (252UL << 8) | (1UL << 20));
                            Emit(0x5c10000000070000UL | 252 | (255UL << 8) | (252UL << 20) | (1UL << 48));
                            Emit(0x5c40000000070000UL | 251 | (251UL << 8) | (252UL << 20) | (7UL << 48));

                            if (maximum)
                            {
                                Emit(0x5c20000000070000UL | target | ((ulong)target << 8) | (251UL << 20) |
                                     (15UL << 39));
                            }
                            else
                            {
                                Emit(0x5c10000000070000UL | target | ((ulong)target << 8) | (251UL << 20));
                            }
                        }

                        break;
                    }
                    case 0xf8c: // ATOMS.POPC.INC.32 with no returned value: one increment per active lane.
                    {
                        uint stride = insn.Bits(78, 2);

                        if (dest != 255 || insn.W1 != 0 || (insn.W2 & ~0xc000U) != 0x0d00003f)
                        {
                            return Fail(index, "native shared increment mode");
                        }

                        uint address = insn.Bits(24, 8);

                        if (stride != 0 && address != 255)
                        {
                            Emit(0x3848000000000000UL | predicate | 251 | ((ulong)address << 8) |
                                 ((ulong)(stride + 1) << 20));
                            address = 251;
                        }

                        MovImm(250, 1);
                        Emit(0xec00000000000000UL | predicate | 255 | ((ulong)address << 8) | (250UL << 20));
                        break;
                    }
                    case 0x38c: // ATOMS.ADD.U32, naturally aligned shared byte offset.
                    {
                        uint offset = insn.Bits(40, 24);

                        if (insn.W2 != 0 || (offset & 3) != 0)
                        {
                            return Fail(index, "native shared atomic mode");
                        }

                        Emit(0xec00000000000000UL | predicate | dest |
                             ((ulong)insn.Bits(24, 8) << 8) | ((ulong)insn.Bits(32, 8) << 20) |
                             ((ulong)(offset >> 2) << 30));
                        break;
                    }
                    case 0x3a8: // ATOMG.E.ADD.STRONG.GPU U32 at a 64-bit register address plus an offset.
                    {
                        uint address = insn.Bits(24, 8);
                        int offset = unchecked((int)(insn.Bits(40, 24) << 8)) >> 8;

                        if (insn.W2 != 0x001ee100 || (address & 1) != 0 || address >= 246 ||
                            offset < -0x80000 || offset > 0x7ffff)
                        {
                            return Fail(index, "native global atomic register mode");
                        }

                        Emit(0xed00000000000000UL | predicate | dest | ((ulong)address << 8) |
                             ((ulong)insn.Bits(32, 8) << 20) | (((ulong)(uint)offset & 0xfffffUL) << 28) |
                             (1UL << 48));
                        break;
                    }
                    case 0x9a8: // ATOMG.E.ADD.STRONG.GPU U32 through a uniform pointer plus an offset.
                    {
                        uint address = UniformRegister(insn.Bits(64, 6));
                        int offset = unchecked((int)(insn.Bits(40, 24) << 8)) >> 8;

                        if (insn.Bits(24, 8) != 255 || offset < -0x80000 || offset > 0x7ffff ||
                            (insn.W2 & ~0x3fU) != 0x081ee140 || address == uint.MaxValue || (address & 1) != 0)
                        {
                            return Fail(index, "native global atomic mode");
                        }

                        Emit(0xed00000000000000UL | predicate | dest | ((ulong)address << 8) |
                             ((ulong)insn.Bits(32, 8) << 20) | (((ulong)(uint)offset & 0xfffffUL) << 28) |
                             (1UL << 48));
                        break;
                    }
                    case 0xb97: // SULD.P.2D/2D_ARRAY.IGN, direct descriptor, channel mask at 72-75.
                    {
                        uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
                        uint coordinate = insn.Bits(24, 8);
                        uint channels = insn.Bits(72, 4);
                        uint dimension = insn.Bits(61, 3); // Maxwell's codes: 3 = 2D, 4 = 2D_ARRAY

                        if ((insn.W2 & ~0xf00U) != 0x001e0000 || channels == 0 ||
                            (dimension != 3 && dimension != 4) || binding >= 18 || offset > 0x1fff ||
                            insn.Bits(59, 2) != 0 || dest == 255 ||
                            coordinate > (dimension == 4 ? 252U : 253U))
                        {
                            return Fail(index, "native formatted surface load mode");
                        }

                        if (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding)
                        {
                            return Fail(index, "native surface descriptor buffer conflict");
                        }

                        result.RequiredTextureBuffer = binding;
                        Emit(Base(0xeb00000000000000UL) | ((ulong)coordinate << 8) |
                             ((ulong)channels << 20) | ((ulong)dimension << 33) | ((ulong)offset << 36) |
                             (1UL << 51));
                        break;
                    }
                    case 0xb9b: // SUST.P.2D/3D.IGN, direct descriptor and all four channels.
                    {
                        uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
                        uint data = insn.Bits(32, 8), coordinate = insn.Bits(24, 8);
                        uint dimension = insn.Bits(61, 3);

                        if (insn.W2 != 0x00100f00 || dimension < 3 || dimension > 5 ||
                            binding >= 18 || offset > 0x1fff || insn.Bits(59, 2) != 0 || data > 252 ||
                            coordinate > (dimension >= 4 ? 252U : 253U))
                        {
                            return Fail(index, "native formatted surface store mode");
                        }

                        if (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding)
                        {
                            return Fail(index, "native surface descriptor buffer conflict");
                        }

                        result.RequiredTextureBuffer = binding;
                        Emit(0xeb20000000000000UL | predicate | data | ((ulong)coordinate << 8) |
                             (15UL << 20) | ((ulong)dimension << 33) | ((ulong)offset << 36) | (1UL << 51));
                        break;
                    }

                    case 0x301: // BREV register.
                    {
                        if (insn.W2 != 0 || insn.W1 > 255)
                        {
                            return Fail(index, "native bit reverse mode");
                        }

                        Emit(Base(0x3828000000000000UL) | ((ulong)insn.W1 << 8) | (1UL << 39) | (1UL << 40));
                        break;
                    }
                    case 0x219:
                    case 0xc19: // U32 register (or uniform, 0xc19) shifts with a zero word outside the selected result.
                    {
                        uint low = insn.Bits(24, 8), high = insn.Bits(64, 8);
                        uint shift = opcode == 0xc19 ? UniformRegister(insn.Bits(32, 8)) : insn.Bits(32, 8);
                        bool right = insn.Bits(76, 1) != 0, highResult = insn.Bits(80, 1) != 0;

                        // SHF.R.U32 of the low word with a zero high word: a plain logical right shift.
                        if (right && !highResult && high == 255 && insn.Bits(73, 2) == 3 &&
                            shift != uint.MaxValue && insn.W1 <= 255 &&
                            (insn.W2 & ~(0x000118ffU | (opcode == 0xc19 ? 0x08000000U : 0U))) == 0x00000600U)
                        {
                            Emit(Base(0x5c28000000000000UL) | ((ulong)low << 8) | ((ulong)shift << 20));
                            break;
                        }

                        if (insn.W1 > 255 || insn.Bits(73, 2) != 3 || shift == uint.MaxValue ||
                            (insn.W2 & ~(0x000118ffU | (opcode == 0xc19 ? 0x08000000U : 0U))) != 0x00000600U ||
                            (right ? (!highResult || low != 255) : (highResult || high != 255)))
                        {
                            return Fail(index, "native register shift mode");
                        }

                        Emit(Base(right ? 0x5cf8000000000000UL : 0x5bf8000000000000UL) |
                             ((ulong)(right ? high : 255U) << 8) | ((ulong)shift << 20) |
                             ((ulong)(right ? 255U : low) << 39) | ((ulong)insn.Bits(75, 1) << 50));
                        break;
                    }
                    case 0x300:
                    case 0xd00: // FLO, including a uniform source.
                    {
                        if ((insn.W2 & ~(0x600U | (opcode == 0xd00 ? 0x08000000U : 0U))) != 0x000e0000U)
                        {
                            return Fail(index, "native leading bit mode");
                        }

                        uint source = Operand(1, false);

                        Emit(Base(0x5c30000000000000UL) | ((ulong)source << 20) |
                             ((ulong)insn.Bits(63, 1) << 40) | ((ulong)insn.Bits(74, 1) << 41) |
                             ((ulong)insn.Bits(73, 1) << 48));
                        break;
                    }
                    case 0x309:
                    case 0xd09: // POPC, including a uniform source.
                    {
                        if (insn.W2 != (opcode == 0xd09 ? 0x08000000U : 0U))
                        {
                            return Fail(index, "native population count mode");
                        }

                        uint source = Operand(1, false);

                        Emit(Base(0x5c08000000000000UL) | ((ulong)source << 20) | ((ulong)insn.Bits(63, 1) << 40));
                        break;
                    }
                    case 0x918:
                    {
                        if (insn.W1 != 0 || insn.W2 != 0)
                        {
                            return Fail(index, "native NOP mode");
                        }

                        Emit(0x50b0000000000f00UL | predicate);
                        break;
                    }
                    case 0x992: // MEMBAR.SC: preserve workgroup/device visibility.
                    {
                        uint scope = insn.Bits(76, 3);

                        if (insn.W1 != 0 || insn.W2 != (scope << 12) ||
                            (scope != 0 && scope != 2 && scope != 3 && scope != 5))
                        {
                            return Fail(index, "native memory barrier scope");
                        }

                        uint legacyScope = scope == 0 ? 0U : scope == 2 ? 1U : scope == 3 ? 2U : 3U;

                        Emit(0xef98000000000000UL | predicate | ((ulong)legacyScope << 8));
                        break;
                    }
                    case 0x9ab: // ERRBAR: wait for prior memory operations on the host backend.
                    {
                        if (insn.W1 != 0 || insn.W2 != 0)
                        {
                            return Fail(index, "native error barrier mode");
                        }

                        Emit(0xef98000000000000UL | predicate | (1UL << 8));
                        break;
                    }
                    case 0xb1d: // BAR.SYNC 0, full workgroup.
                    {
                        if (insn.W1 != 0 || insn.W2 != 0 || nativePredicate != 7)
                        {
                            return Fail(index, "native workgroup barrier mode");
                        }

                        Emit(0xf0a8000000000000UL | predicate | (1UL << 43) | (1UL << 44) |
                             (1UL << 39) | (7UL << 39));
                        break;
                    }
                    case 0x388:
                    case 0x984: // Shared memory load/store with scaled register offsets.
                    {
                        uint width = insn.Bits(73, 3), stride = insn.Bits(78, 2);

                        // LDS [Ra.Xn + URb + offset]: bit 91 adds a uniform register (bits 32-39).
                        bool uniformBase = opcode == 0x984 && insn.Bits(91, 1) != 0;
                        uint baseRegister = uniformBase ? UniformRegister(insn.Bits(32, 8)) : 255U;

                        if (width > 6 ||
                            (insn.W2 & ~(uniformBase ? 0x08000000U : 0U)) != ((width << 9) | (stride << 14)) ||
                            (opcode == 0x984 && !uniformBase && insn.Bits(32, 8) != 0) ||
                            baseRegister == uint.MaxValue)
                        {
                            return Fail(index, "native shared memory addressing mode");
                        }

                        uint address = insn.Bits(24, 8);

                        if (stride != 0 && address != 255)
                        {
                            Emit(0x3848000000000000UL | predicate | 250 |
                                 ((ulong)address << 8) | ((ulong)(stride + 1) << 20));
                            address = 250;
                        }

                        if (baseRegister != 255)
                        {
                            Emit(0x5c10000000000000UL | predicate | 250 | ((ulong)address << 8) |
                                 ((ulong)baseRegister << 20));
                            address = 250;
                        }

                        uint data = opcode == 0x388 ? insn.Bits(32, 8) : dest;

                        Emit((opcode == 0x388 ? 0xef58000000000000UL : 0xef48000000000000UL) |
                             predicate | data | ((ulong)address << 8) | ((ulong)insn.Bits(40, 24) << 20) |
                             ((ulong)width << 48));
                        break;
                    }
                    case 0x83b: // LDSM.16.M88.{1,2,4} (ldmatrix): 8x8 16-bit matrices from shared memory.
                    {
                        // Lanes 8m..8m+7 give the row addresses of matrix m; lane t receives the 32 bits at
                        // row t/4, byte (t%4)*4 of each matrix in Rd+m. Emulated with SHFL.IDX + LDS.32.
                        uint count = 1U << (int)insn.Bits(72, 2);
                        bool transpose = insn.Bits(78, 1) != 0;

                        if (insn.Bits(72, 2) == 3 || (insn.W2 & ~0x4300U) != 0 || insn.Bits(32, 8) != 0 ||
                            dest + count > 246)
                        {
                            return Fail(index, "native matrix load mode");
                        }

                        uint address = insn.Bits(24, 8);
                        ulong offset = insn.Bits(40, 24);

                        if (address != 255 && address >= dest && address < dest + count)
                        {
                            Emit(0x5c10000000000000UL | predicate | 253 | ((ulong)address << 8) | (255UL << 20));
                            address = 253;
                        }

                        if (transpose)
                        {
                            // .MT88: lane t receives stored rows 2(t%4) and 2(t%4)+1 at column t/4 (16 bits each).
                            Emit(0xf0c8000000000000UL | predicate | 246); // laneid
                            Emit(0x0400000000000000UL | predicate | 247 | (246UL << 8) | (3UL << 20));
                            Emit(0x3848000000000000UL | predicate | 247 | (247UL << 8) | (1UL << 20)); // row 2(t%4)
                            Emit(0x3828000000000000UL | predicate | 248 | (246UL << 8) | (2UL << 20));
                            Emit(0x3848000000000000UL | predicate | 248 | (248UL << 8) | (1UL << 20)); // byte 2(t/4)

                            for (uint matrix = 0; matrix < count; ++matrix)
                            {
                                for (uint half = 0; half < 2; ++half)
                                {
                                    Emit(0x1c00000000000000UL | predicate | 249 | (247UL << 8) |
                                         ((ulong)(matrix * 8 + half) << 20));
                                    Emit(0xef10000000000000UL | predicate | 250 | ((ulong)address << 8) |
                                         (249UL << 20) | (1UL << 29) | (31UL << 34) | (7UL << 48));
                                    Emit(0x5c10000000000000UL | predicate | 250 | (250UL << 8) | (248UL << 20));
                                    Emit(0xef48000000000000UL | predicate | (251 + half) | (250UL << 8) |
                                         (offset << 20) | (2UL << 48)); // LDS.U16
                                }

                                Emit(0x5c18000000000000UL | predicate | (dest + matrix) | (252UL << 8) |
                                     (251UL << 20) | (16UL << 39));
                            }

                            break;
                        }

                        Emit(0xf0c8000000000000UL | predicate | 250); // S2R laneid
                        Emit(0x3828000000000000UL | predicate | 251 | (250UL << 8) | (2UL << 20)); // row
                        Emit(0x0400000000000000UL | predicate | 250 | (250UL << 8) | (3UL << 20));
                        Emit(0x3848000000000000UL | predicate | 250 | (250UL << 8) | (2UL << 20)); // column bytes

                        for (uint matrix = 0; matrix < count; ++matrix)
                        {
                            uint lane = 251;

                            if (matrix != 0)
                            {
                                Emit(0x1c00000000000000UL | predicate | 252 | (251UL << 8) |
                                     ((ulong)(matrix * 8) << 20));
                                lane = 252;
                            }

                            Emit(0xef10000000000000UL | predicate | 253 | ((ulong)address << 8) |
                                 ((ulong)lane << 20) | (1UL << 29) | (31UL << 34) | (7UL << 48));
                            Emit(0x5c10000000000000UL | predicate | 253 | (253UL << 8) | (250UL << 20));
                            Emit(0xef48000000000000UL | predicate | (dest + matrix) | (253UL << 8) |
                                 (offset << 20) | (4UL << 48));
                        }

                        break;
                    }
                    case 0x919:
                    case 0x9c3: // S2R / S2UR thread and workgroup identities.
                    {
                        uint special = insn.Bits(72, 8);

                        if (insn.W1 != 0 || insn.W2 != (special << 8) ||
                            (special != 0 && special != 33 && special != 34 && special != 35 &&
                             special != 37 && special != 38 && special != 39 &&
                             special != 56 && special != 57 && special != 58 && special != 59 && special != 60))
                        {
                            return Fail(index, "native special register");
                        }

                        uint target = opcode == 0x9c3 ? UniformRegister(dest) : dest;

                        if (target == uint.MaxValue)
                        {
                            return Fail(index, "native uniform special destination");
                        }

                        Emit(0xf0c8000000000000UL | predicate | target | ((ulong)special << 20));
                        break;
                    }
                    case 0x28c:
                    case 0x88c: // UISETP, signed (bit 73) or unsigned, with UPT accumulation.
                    {
                        uint output = insn.Bits(81, 3);
                        uint a = UniformRegister(insn.Bits(24, 8));
                        uint b = opcode == 0x88c ? 252U : UniformRegister(insn.Bits(32, 8));

                        if (output > 6 || insn.Bits(84, 3) != 7 || insn.Bits(87, 4) != 7 ||
                            (insn.Bits(72, 4) & ~2U) != 0 || insn.Bits(12, 4) != 7 ||
                            a == uint.MaxValue || b == uint.MaxValue)
                        {
                            return Fail(index, "native uniform comparison mode");
                        }

                        uint temporary = 6 - output, mask = 1U << (int)temporary;

                        if (opcode == 0x88c)
                        {
                            MovImm(252, insn.W1);
                        }

                        Emit(0x38e8000000070000UL | 253 | (255UL << 8) | ((ulong)mask << 20));
                        Emit(0x5b60000000070000UL | 7 | ((ulong)temporary << 3) |
                             ((ulong)a << 8) | ((ulong)b << 20) | (7UL << 39) |
                             ((ulong)insn.Bits(73, 1) << 48) | ((ulong)insn.Bits(76, 3) << 49));
                        Emit(0x38e8000000070000UL | 239 | (239UL << 8) | ((ulong)mask << 20));
                        Emit(0x38f0000000070000UL | (253UL << 8) | ((ulong)mask << 20));
                        break;
                    }
                    case 0x287:
                    case 0x887: // USEL, UP0/UP1; preserve ordinary predicates.
                    {
                        uint condition = insn.Bits(87, 3);
                        uint target = UniformRegister(dest);
                        uint a = UniformRegister(insn.Bits(24, 8));
                        uint b = opcode == 0x887 ? 252U : UniformRegister(insn.Bits(32, 8));

                        if (condition > 6 || target == uint.MaxValue || a == uint.MaxValue ||
                            b == uint.MaxValue || insn.Bits(12, 4) != 7 ||
                            (insn.W2 & ~0x07800000U) != 0x08000000U)
                        {
                            return Fail(index, "native uniform select mode");
                        }

                        if (opcode == 0x887 && index > 0 && condition == 0 && insn.W1 <= 0x7ffff)
                        {
                            AmpereInstruction prior = code[(int)(index - 1)];

                            if (prior.Bits(0, 12) == 0x88c &&
                                prior.Bits(24, 8) == insn.Bits(24, 8) &&
                                prior.W1 == insn.W1 && prior.W2 == 0x0bf01070 &&
                                insn.W2 == 0x08000000)
                            {
                                // Preserve the unsigned clamp idiom for indirect-table analysis.
                                Emit(0x3820000000070000UL | target | ((ulong)a << 8) |
                                     ((ulong)insn.W1 << 20) | (7UL << 39));
                                break;
                            }
                        }

                        if (opcode == 0x887)
                        {
                            MovImm(252, insn.W1);
                        }

                        uint temporary = 6 - condition, mask = 1U << (int)temporary;

                        Emit(0x38e8000000070000UL | 253 | (255UL << 8) | ((ulong)mask << 20));
                        Emit(0x38f0000000070000UL | (239UL << 8) | ((ulong)mask << 20));
                        Emit(0x5ca0000000070000UL | target | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)temporary << 39) | ((ulong)insn.Bits(90, 1) << 42));
                        Emit(0x38f0000000070000UL | (253UL << 8) | ((ulong)mask << 20));
                        break;
                    }
                    case 0x381:
                    case 0x981: // LDG.E of 8 to 128 bits, register or uniform 64-bit address.
                    {
                        // Sizes as Maxwell's: U8, S8, U16, S16, 32, 64, 128.
                        uint size = insn.Bits(73, 3);
                        uint address = insn.Bits(24, 8);

                        if (size > 6 || (opcode == 0x381 && insn.Bits(32, 8) != 0) ||
                            (size == 5 && (dest & 1) != 0) || (size == 6 && (dest & 3) != 0))
                        {
                            return Fail(index, "native global load mode");
                        }

                        if (opcode == 0x381)
                        {
                            // Bit 77 (strong/scoped ordering) needs nothing extra from a Maxwell LDG.
                            if ((insn.W2 & ~0x2e00U) != 0x001e8100 || (address & 1) != 0 || address >= 246)
                            {
                                return Fail(index, "native global load mode");
                            }
                        }
                        else
                        {
                            // The uniform base sits where a register source would (bits 32-37).
                            uint uniformAddress = insn.Bits(38, 2) != 0
                                ? uint.MaxValue
                                : UniformRegister(insn.Bits(32, 6));

                            if ((insn.W2 & ~(0x04000000U | 0x3fU | 0xe00U | 0x2000U)) != 0x081e8100 ||
                                uniformAddress == uint.MaxValue || (uniformAddress & 1) != 0)
                            {
                                return Fail(index, "native global load uniform addressing mode");
                            }

                            if (address == 255)
                            {
                                address = uniformAddress;
                            }
                            else
                            {
                                if (insn.Bits(90, 1) != 0 && ((address & 1) != 0 || address >= 246))
                                {
                                    return Fail(index, "native global load register address pair");
                                }

                                Emit(0x5c10000000000000UL | predicate | 250 | ((ulong)uniformAddress << 8) |
                                     ((ulong)address << 20) | (1UL << 47));
                                Emit(0x5c10000000000000UL | predicate | 251 |
                                     ((ulong)(uniformAddress + 1) << 8) |
                                     ((ulong)(insn.Bits(90, 1) != 0 ? address + 1 : 255U) << 20) |
                                     (1UL << 43));
                                address = 250;
                            }
                        }

                        Emit(Base(0xeed0000000000000UL) | ((ulong)address << 8) |
                             ((ulong)insn.Bits(40, 24) << 20) | (1UL << 45) | ((ulong)size << 48));
                        break;
                    }
                    case 0x386:
                    case 0x986: // Global store of 32/64/128 bits, 64-bit address.
                    {
                        uint data = insn.Bits(32, 8);
                        uint size = insn.Bits(73, 3);

                        if (size > 6 ||
                            (data != 255 && ((size == 5 && (data & 1) != 0) ||
                                             (size == 6 && (data & 3) != 0) ||
                                             data + (size < 4 ? 1U : 1U << (int)(size - 4)) > 246)))
                        {
                            return Fail(index, "native global store size");
                        }

                        if (data == 255 && size > 4)
                        {
                            // Zeros wider than one register: store from zeroed scratch registers (aligned to the
                            // access: R248-R251 for 128 bits, R250-R251 for 64).
                            data = size == 6 ? 248U : 250U;

                            for (uint i = 0; i < (1U << (int)(size - 4)); ++i)
                            {
                                MovImm(data + i, 0);
                            }
                        }

                        uint address = insn.Bits(24, 8);
                        bool extended = insn.Bits(72, 1) != 0;

                        if (opcode == 0x986)
                        {
                            uint uniformAddress = UniformRegister(insn.Bits(64, 6));

                            if ((insn.W2 & ~(0x04000000U | 0x3fU | 0xe00U)) != 0x0810a100 ||
                                uniformAddress == uint.MaxValue || (uniformAddress & 1) != 0 || !extended)
                            {
                                return Fail(index, "native global store uniform addressing mode");
                            }

                            if (address == 255)
                            {
                                address = uniformAddress;
                            }
                            else
                            {
                                if (insn.Bits(90, 1) != 0 && ((address & 1) != 0 || address >= 254))
                                {
                                    return Fail(index, "native global store register address pair");
                                }

                                Emit(0x5c10000000000000UL | predicate | 250 | ((ulong)uniformAddress << 8) |
                                     ((ulong)address << 20) | (1UL << 47));
                                Emit(0x5c10000000000000UL | predicate | 251 |
                                     ((ulong)(uniformAddress + 1) << 8) |
                                     ((ulong)(insn.Bits(90, 1) != 0 ? address + 1 : 255U) << 20) |
                                     (1UL << 43));
                                address = 250;
                            }
                        }
                        else if ((insn.W2 & ~0xe00U) != 0x0010a100)
                        {
                            return Fail(index, "native global store register addressing mode");
                        }

                        Emit(0xeed8000000000000UL | predicate | data | ((ulong)address << 8) |
                             ((ulong)insn.Bits(40, 24) << 20) | ((ulong)(extended ? 1U : 0U) << 45) |
                             ((ulong)size << 48));
                        break;
                    }
                    case 0x95b: // Fragment discard; execution predicate is preserved.
                    {
                        if (insn.W1 != 0 || insn.W2 != 0x03800000)
                        {
                            return Fail(index, "native fragment discard mode");
                        }

                        Emit(0xe33000000000000fUL | predicate);
                        break;
                    }
                    case 0x305:
                    case 0xb05:
                    case 0xd05: // F2I NTZ, register/constant-buffer/uniform source
                    {
                        uint mode = insn.W2 & ~(opcode == 0xd05 ? 0x08000000U : 0U);
                        bool half = opcode == 0x305 && mode == 0x0010f000;
                        bool small = mode == 0x00212800;
                        bool floor = (mode & ~0x10100U) == 0x00207000;
                        bool nearest = (mode & ~0x10100U) == 0x00203000;

                        if ((!half && !small && !floor && !nearest && (mode & ~0x100U) != 0x0021f000) ||
                            (opcode != 0xb05 && (insn.W1 & 0xffffff00U) != 0) ||
                            (opcode == 0xd05 && UniformRegister(insn.Bits(32, 8)) == uint.MaxValue))
                        {
                            return Fail(index, "native float-to-integer conversion mode");
                        }

                        bool isSigned = insn.Bits(72, 1) != 0;
                        uint source = opcode == 0xb05 ? Materialize(2, 0, 252, false, false)
                                     : opcode == 0xd05 ? UniformRegister(insn.Bits(32, 8)) : insn.Bits(32, 8);

                        if (half)
                        {
                            ExtractHalf(new HalfSource(source, 2, false, false), 0, 252);
                            source = 252;
                        }

                        if (!isSigned)
                        {
                            // Clear NaNs without consuming a guest predicate. Clamp the
                            // conversion below 2^32, retaining an overflow mask for saturation.
                            Emit(0x5800000000000000UL | predicate | 250 | ((ulong)source << 8) |
                                 ((ulong)source << 20) | (7UL << 39) | (7UL << 48));
                            Emit(0x5c40000000000000UL | predicate | 250 | ((ulong)source << 8) |
                                 (250UL << 20) | (7UL << 48));

                            if (!small)
                            {
                                MovImm(252, 0x4f800000);
                                Emit(0x5800000000000000UL | predicate | 251 | (250UL << 8) |
                                     (252UL << 20) | (7UL << 39) | (6UL << 48));
                                MovImm(252, 0x4f7fffff);
                                Emit(0x5c60000000000000UL | predicate | 250 | (250UL << 8) |
                                     (252UL << 20) | (7UL << 39));
                            }

                            source = 250;
                        }

                        Emit(Base(0x5cb0000000000000UL) | ((ulong)(small ? 1U : 2U) << 8) | (2UL << 10) |
                             ((ulong)(isSigned ? 1U : 0U) << 12) | ((ulong)source << 20) |
                             ((ulong)(small || nearest ? 0U : floor ? 1U : 3U) << 39) |
                             ((ulong)insn.Bits(80, 1) << 44));

                        if (!isSigned && !small)
                        {
                            Emit(Base(0x5c40000000000000UL) | (dest << 8) | (251UL << 20) |
                                 (1UL << 41) | (7UL << 48));
                        }

                        break;
                    }
                    case 0xb6f: // TXQ texture dimensions, two packed scalar outputs.
                    {
                        if (insn.Bits(59, 5) != 0 || insn.Bits(64, 8) != 255 || insn.Bits(72, 4) == 0 ||
                            insn.Bits(72, 4) > 3 || insn.Bits(76, 14) != 0 || insn.Bits(91, 1) != 0 ||
                            insn.Bits(54, 5) != 2 || insn.Bits(40, 14) > 0x1fff ||
                            (insn.Bits(72, 4) == 3 && (dest & 1) != 0) || dest >= 254)
                        {
                            return Fail(index, "native texture dimension query mode");
                        }

                        result.RequiredTextureBuffer = 2;
                        Emit(0xdf48000000000000UL | predicate | dest | ((ulong)insn.Bits(24, 8) << 8) |
                             (1UL << 22) | ((ulong)insn.Bits(72, 4) << 31) |
                             ((ulong)insn.Bits(40, 14) << 36) | ((ulong)insn.Bits(90, 1) << 49));
                        break;
                    }
                    case 0xb66: // TLD.SCR.LZ, direct texel fetch
                    {
                        uint mask = insn.Bits(72, 4);
                        uint dimension = insn.Bits(61, 3);
                        bool array = dimension == 5;
                        bool volume = dimension == 2;
                        uint sourceA = insn.Bits(24, 8), sourceB = insn.Bits(32, 8);
                        uint secondDest = insn.Bits(64, 8);
                        bool split = secondDest != 255;
                        uint[] rgMasks = [1, 2, 4, 8, 3, 9, 10, 12];
                        uint[] rgbaMasks = [7, 11, 13, 14, 15];
                        uint swizzle = uint.MaxValue;

                        if (split)
                        {
                            for (uint i = 0; i < rgbaMasks.Length; ++i)
                            {
                                if (rgbaMasks[i] == mask)
                                {
                                    swizzle = i;
                                }
                            }
                        }
                        else
                        {
                            for (uint i = 0; i < rgMasks.Length; ++i)
                            {
                                if (rgMasks[i] == mask)
                                {
                                    swizzle = i;
                                }
                            }
                        }

                        if (insn.Bits(60, 1) == 0 || (dimension != 0 && dimension != 1 && !array && !volume) ||
                            swizzle == uint.MaxValue || insn.Bits(76, 5) != 0 ||
                            insn.Bits(81, 3) != 7 || insn.Bits(87, 3) != 1 || insn.Bits(59, 1) != 0 ||
                            insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) >= 18 || dest == 255 ||
                            ((mask & (mask - 1)) != 0 && ((dest & 1) != 0 || dest >= 254)) ||
                            (split && mask == 15 && ((secondDest & 1) != 0 || secondDest >= 254)) ||
                            (array && ((sourceA & 1) != 0 || sourceA >= 249)) ||
                            (volume && ((sourceA & 1) != 0 || sourceA >= 254)))
                        {
                            return Fail(index, "native texel fetch mode or descriptor binding");
                        }

                        uint binding = insn.Bits(54, 5);

                        if (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding)
                        {
                            return Fail(index, "native texture descriptor buffer conflict");
                        }

                        result.RequiredTextureBuffer = binding;

                        if (array)
                        {
                            Emit(0x5c98000000000000UL | predicate | 250 | (15UL << 39) | ((ulong)(sourceA + 1) << 20));
                            Emit(0x5c98000000000000UL | predicate | 251 | (15UL << 39) | ((ulong)sourceB << 20));
                            sourceB = 250;
                        }

                        Emit(0xda00000000000000UL | predicate | dest | ((ulong)sourceA << 8) |
                             ((ulong)sourceB << 20) | ((ulong)insn.Bits(64, 8) << 28) |
                             ((ulong)insn.Bits(40, 14) << 36) | ((ulong)insn.Bits(90, 1) << 49) |
                             ((ulong)swizzle << 50) |
                             ((ulong)(array ? 8U : volume ? 7U : dimension == 0 ? 0U : 2U) << 53));
                        break;
                    }
                    case 0x91a: // DEPBAR.LE SB5, 1: scoreboard wait, represented by IR data dependencies.
                    {
                        if (insn.W0 != 0x791a || (insn.W1 & ~0xfc0U) != 0xd000 || insn.W2 != 0)
                        {
                            return Fail(index, "native dependency barrier mode");
                        }

                        break;
                    }
                    case 0xb69: // TMML (LOD query), 2D. Results are 8.8 fixed point, as on Maxwell.
                    {
                        uint mask = insn.Bits(72, 4), secondDest = insn.Bits(64, 8);
                        uint sourceA = insn.Bits(24, 8), sourceB = insn.Bits(32, 8);
                        uint binding = insn.Bits(54, 5);

                        if (insn.Bits(61, 3) != 1 || insn.Bits(76, 4) != 0 || insn.Bits(80, 10) != 0 ||
                            insn.Bits(59, 1) != 0 || insn.Bits(40, 14) > 0x1fff || binding >= 18 ||
                            mask == 0 || sourceA >= 246 ||
                            (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding))
                        {
                            return Fail(index, "native texture LOD query mode");
                        }

                        result.RequiredTextureBuffer = binding;

                        // Coordinates: (Ra, Ra+1) when Rb is RZ, else (Ra, Rb).
                        Emit(0x5c98000000000000UL | predicate | 250 | (15UL << 39) | ((ulong)sourceA << 20));
                        Emit(0x5c98000000000000UL | predicate | 251 | (15UL << 39) |
                             ((ulong)(sourceB == 255 ? sourceA + 1 : sourceB) << 20));
                        Emit(0xdf58000000000000UL | predicate | 250 | (250UL << 8) | (255UL << 20) |
                             (2UL << 28) | ((ulong)mask << 31) | ((ulong)insn.Bits(40, 14) << 36) |
                             ((ulong)insn.Bits(90, 1) << 49));

                        uint components = PopCount(mask);
                        bool split = secondDest != 255;

                        for (uint i = 0; i < components; ++i)
                        {
                            uint target = 255;

                            if (!split || i < 2)
                            {
                                target = dest == 255 ? 255U : dest + i;
                            }
                            else
                            {
                                target = secondDest + (i - 2);
                            }

                            if (target != 255)
                            {
                                Emit(0x5c98000000000000UL | predicate | target | (15UL << 39) |
                                     ((ulong)(250 + i) << 20));
                            }
                        }

                        break;
                    }
                    case 0xb63: // TLD4 (gather), 2D without offsets or depth compare; component at bits 87-88.
                    {
                        uint mask = insn.Bits(72, 4), secondDest = insn.Bits(64, 8);
                        uint sourceA = insn.Bits(24, 8), sourceB = insn.Bits(32, 8);
                        uint binding = insn.Bits(54, 5);

                        if (insn.Bits(61, 3) != 1 || insn.Bits(76, 4) != 0 || insn.Bits(81, 3) != 7 ||
                            insn.Bits(59, 1) != 0 || insn.Bits(40, 14) > 0x1fff || binding >= 18 ||
                            insn.Bits(89, 1) != 0 || mask == 0 ||
                            (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding))
                        {
                            return Fail(index, "native texture gather mode");
                        }

                        result.RequiredTextureBuffer = binding;

                        // Two coordinates: x in Ra, y in Rb (the coordinate list splits evenly across them).
                        Emit(0x5c98000000000000UL | predicate | 250 | (15UL << 39) | ((ulong)sourceA << 20));
                        Emit(0x5c98000000000000UL | predicate | 251 | (15UL << 39) |
                             ((ulong)(sourceB == 255 ? 255U : sourceB) << 20));
                        Emit(0xc800000000000000UL | predicate | 250 | (250UL << 8) | (255UL << 20) |
                             (2UL << 28) | ((ulong)mask << 31) | ((ulong)insn.Bits(40, 14) << 36) |
                             ((ulong)insn.Bits(90, 1) << 49) | (7UL << 51) |
                             ((ulong)insn.Bits(87, 2) << 56));

                        uint components = PopCount(mask);
                        bool split = secondDest != 255;

                        for (uint i = 0; i < components; ++i)
                        {
                            uint target = 255;

                            if (!split || i < 2)
                            {
                                target = dest == 255 ? 255U : dest + i;
                            }
                            else
                            {
                                target = secondDest + (i - 2);
                            }

                            if (target != 255)
                            {
                                Emit(0x5c98000000000000UL | predicate | target | (15UL << 39) |
                                     ((ulong)(250 + i) << 20));
                            }
                        }

                        break;
                    }
                    case 0xb60: // TEX, scalar 2D implicit-LOD sample
                    {
                        uint mask = insn.Bits(72, 4);
                        uint secondDest = insn.Bits(64, 8);
                        uint[] rgMasks = [1, 2, 4, 8, 3, 9, 10, 12];
                        uint[] rgbaMasks = [7, 11, 13, 14, 15];
                        uint swizzle = uint.MaxValue;
                        bool split = secondDest != 255;
                        bool depthCompare = insn.Bits(78, 1) != 0;
                        uint dimension = insn.Bits(61, 3), lod = insn.Bits(87, 3);
                        uint sourceA = insn.Bits(24, 8), sourceB = insn.Bits(32, 8);

                        if (dimension == 5 && depthCompare && lod == 0 && !split && mask == 1)
                        {
                            if (insn.Bits(60, 1) == 0 || insn.Bits(76, 2) != 0 || insn.Bits(81, 3) != 7 ||
                                insn.Bits(59, 1) != 0 || insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) != 2 ||
                                (sourceA & 1) != 0 || (sourceB & 1) != 0 || sourceA >= 248 ||
                                sourceB >= 248 || dest >= 248)
                            {
                                return Fail(index, "native array depth comparison sample mode");
                            }

                            result.RequiredTextureBuffer = 2;

                            for (uint i = 0; i < 3; ++i)
                            {
                                uint source = i < 2 ? sourceA + i : sourceB;
                                Emit(0x5c98000000000000UL | predicate | (250 + i) | (15UL << 39) |
                                     ((ulong)source << 20));
                            }

                            Emit(0xc000000000000000UL | predicate | dest | (250UL << 8) |
                                 ((ulong)(sourceB + 1) << 20) | (3UL << 28) | (1UL << 31) |
                                 ((ulong)insn.Bits(40, 14) << 36) | ((ulong)insn.Bits(90, 1) << 49) |
                                 (1UL << 50) | (7UL << 51));
                            break;
                        }

                        // General form: gather the native operand list (array index first, coordinates,
                        // then LOD/bias and the depth reference) into Maxwell TEX's coordinate and meta
                        // registers, sample into scratch and copy the packed components out. Native packs
                        // up to four values as halves of Ra and Rb (1+1, 2+1, 2+2), five or more as 4+n.
                        bool GeneralTex()
                        {
                            uint[] coordinateCount = [1, 2, 3, 3, 2, 3, 0, 4];
                            uint[] maxwellType = [0, 2, 4, 6, 1, 3, 0, 7];
                            bool offsets = insn.Bits(76, 1) != 0;

                            if (dimension == 6 || insn.Bits(77, 1) != 0 || insn.Bits(81, 3) != 7 ||
                                insn.Bits(59, 1) != 0 || insn.Bits(40, 14) > 0x1fff ||
                                insn.Bits(54, 5) >= 18 || (lod > 3 && lod != 6 && lod != 7) ||
                                (depthCompare && mask != 1) || mask == 0)
                            {
                                return false;
                            }

                            uint coordinates = coordinateCount[dimension];
                            bool hasLod = lod == 2 || lod == 3 || lod == 6 || lod == 7;
                            uint count = coordinates + (hasLod ? 1U : 0U) + (offsets ? 1U : 0U) +
                                         (depthCompare ? 1U : 0U);
                            uint inA = count <= 4 ? (count + 1) / 2 : 4;

                            uint Value(uint i)
                            {
                                if (i < inA)
                                {
                                    return sourceA == 255 ? 255U : sourceA + i;
                                }

                                return sourceB == 255 ? 255U : sourceB + (i - inA);
                            }

                            if ((sourceA != 255 && sourceA + inA > 246) ||
                                (count > inA && sourceB != 255 && sourceB + (count - inA) > 246))
                            {
                                return false;
                            }

                            uint binding = insn.Bits(54, 5);

                            if (result.RequiredTextureBuffer != uint.MaxValue && result.RequiredTextureBuffer != binding)
                            {
                                return false;
                            }

                            result.RequiredTextureBuffer = binding;

                            void Copy(uint target, uint source)
                            {
                                Emit(0x5c98000000000000UL | predicate | target | (15UL << 39) | ((ulong)source << 20));
                            }

                            // Coordinates to R250.., then LOD/bias, offsets and reference to R246...
                            for (uint i = 0; i < coordinates; ++i)
                            {
                                Copy(250 + i, Value(i));
                            }

                            uint meta = 246;
                            uint next = coordinates;

                            if (hasLod)
                            {
                                Copy(meta++, Value(next++));
                            }

                            if (offsets)
                            {
                                Copy(meta++, Value(next++));
                            }

                            if (depthCompare)
                            {
                                Copy(meta++, Value(next++));
                            }

                            Emit(0xc000000000000000UL | predicate | 250 | (250UL << 8) |
                                 ((ulong)(meta == 246 ? 255U : 246U) << 20) |
                                 ((ulong)maxwellType[dimension] << 28) | ((ulong)mask << 31) |
                                 ((ulong)insn.Bits(40, 14) << 36) | ((ulong)insn.Bits(90, 1) << 49) |
                                 ((ulong)(depthCompare ? 1U : 0U) << 50) | (7UL << 51) |
                                 ((ulong)(offsets ? 1U : 0U) << 54) | ((ulong)lod << 55));

                            uint components = PopCount(mask);

                            if (insn.Bits(79, 1) != 0)
                            {
                                // FP16 results: (c0, c1) packed into the first destination, (c2, c3) into
                                // the second (F2F writes the half in the low 16 bits; ISCADD merges).
                                void Pack(uint target, uint low, uint high)
                                {
                                    if (target == 255)
                                    {
                                        return;
                                    }

                                    Emit(0x5ca8000000000000UL | predicate | 246 | (1UL << 8) |
                                         (2UL << 10) | ((ulong)low << 20));
                                    Emit(0x5ca8000000000000UL | predicate | 247 | (1UL << 8) |
                                         (2UL << 10) | ((ulong)high << 20));
                                    Emit(0x5c18000000000000UL | predicate | target | (246UL << 8) |
                                         (247UL << 20) | (16UL << 39));
                                }

                                Pack(dest, 250, components > 1 ? 251U : 255U);

                                if (components > 2)
                                {
                                    Pack(split ? secondDest : (dest == 255 ? 255U : dest + 1),
                                        252, components > 3 ? 253U : 255U);
                                }

                                return true;
                            }

                            for (uint i = 0; i < components; ++i)
                            {
                                uint target = 255;

                                if (!split)
                                {
                                    target = dest == 255 ? 255U : dest + i;
                                }
                                else if (i < 2)
                                {
                                    target = dest == 255 ? 255U : dest + i;
                                }
                                else
                                {
                                    target = secondDest + (i - 2);
                                }

                                if (target != 255)
                                {
                                    Copy(target, 250 + i);
                                }
                            }

                            return true;
                        }

                        if (split)
                        {
                            for (uint i = 0; i < rgbaMasks.Length; ++i)
                            {
                                if (rgbaMasks[i] == mask)
                                {
                                    swizzle = i;
                                }
                            }
                        }
                        else
                        {
                            for (uint i = 0; i < rgMasks.Length; ++i)
                            {
                                if (rgMasks[i] == mask)
                                {
                                    swizzle = i;
                                }
                            }
                        }

                        // SM70 scalar coordinates and split RG/BA destinations match TEXS.
                        // Descriptor offsets are words in both encodings, but only native
                        // explicitly encodes the constant-buffer binding.
                        if (insn.Bits(60, 1) == 0 || (dimension != 1 && dimension != 5) ||
                            swizzle == uint.MaxValue || insn.Bits(76, 2) != 0 ||
                            insn.Bits(81, 3) != 7 ||
                            (lod > 1 && (lod != 3 || dimension != 1 || depthCompare)) ||
                            (lod == 3 && ((sourceA & 1) != 0 || sourceA >= 249)) ||
                            insn.Bits(59, 1) != 0 || insn.Bits(40, 14) > 0x1fff ||
                            insn.Bits(54, 5) != 2 || dest == 255 ||
                            ((mask & (mask - 1)) != 0 && ((dest & 1) != 0 || dest >= 254)) ||
                            (split && mask == 15 && ((secondDest & 1) != 0 || secondDest >= 254)) ||
                            (depthCompare && (dimension != 1 || split || mask != 1 ||
                                              (sourceA & 1) != 0 || sourceA >= 249)) ||
                            (dimension == 5 && ((sourceA & 1) != 0 || sourceA >= 249)))
                        {
                            if (!GeneralTex())
                            {
                                return Fail(index, "native texture mode or descriptor binding");
                            }

                            break;
                        }

                        result.RequiredTextureBuffer = 2;
                        Emit((0xd800000000000000UL & ~((ulong)insn.Bits(79, 1) << 59)) | predicate | dest |
                             ((ulong)insn.Bits(24, 8) << 8) | ((ulong)insn.Bits(32, 8) << 20) |
                             ((ulong)insn.Bits(64, 8) << 28) | ((ulong)insn.Bits(40, 14) << 36) |
                             ((ulong)insn.Bits(90, 1) << 49) | ((ulong)swizzle << 50) |
                             ((ulong)(dimension == 5 ? (lod != 0 ? 8U : 7U)
                                 : depthCompare ? (lod != 0 ? 6U : 4U)
                                 : lod == 3 ? 3U : lod != 0 ? 2U : 1U) << 53));
                        break;
                    }
                    case 0x816: // Captured PRMT half-word concatenation
                    {
                        if (insn.W1 > 0xffff || (insn.W2 & ~0xffU) != 0)
                        {
                            return Fail(index, "native permutation mode");
                        }

                        if (insn.W1 != 0x5410 && insn.W1 != 0x5432 && insn.W1 != 0x7610 && insn.W1 != 0x7632)
                        {
                            // Byte i of the result is byte (s & 7) of c:a, or its sign bit repeated when s & 8.
                            uint a = insn.Bits(24, 8), c = insn.Bits(64, 8);

                            if (a >= 250 && a != 255)
                            {
                                return Fail(index, "native permutation source");
                            }

                            MovImm(250, 0);

                            for (uint i = 0; i < 4; ++i)
                            {
                                uint select = (insn.W1 >> (int)(i * 4)) & 0xf;
                                uint source = (select & 4) != 0 ? c : a;
                                uint position = (select & 3) * 8;

                                Emit(0x3800000000000000UL | predicate | 251 | ((ulong)source << 8) |
                                     ((ulong)((select & 8) != 0 ? (position + 7) | (1U << 8) : position | (8U << 8)) << 20) |
                                     ((ulong)((select >> 3) & 1) << 48));
                                Emit(0x36f0000000000000UL | predicate | 250 | (251UL << 8) |
                                     ((ulong)((i * 8) | (8U << 8)) << 20) | (250UL << 39));
                            }

                            Emit(Base(0x5c10000000000000UL) | (250UL << 8) | (255UL << 20));
                            break;
                        }

                        uint aReg = insn.Bits(24, 8), bReg = insn.Bits(64, 8);

                        if (insn.W1 != 0x5432 && insn.W1 != 0x7632)
                        {
                            Emit(0x0400000000000000UL | predicate | 250 | ((ulong)aReg << 8) | (0xffffUL << 20));
                        }
                        else
                        {
                            Emit(0x3828000000000000UL | predicate | 250 | ((ulong)aReg << 8) | (16UL << 20));
                        }

                        if (insn.W1 == 0x7610 || insn.W1 == 0x7632)
                        {
                            Emit(0x0400000000000000UL | predicate | 251 | ((ulong)bReg << 8) | (0xffff0000UL << 20));
                            Emit(Base(0x5c10000000000000UL) | (250UL << 8) | (251UL << 20));
                        }
                        else
                        {
                            Emit(Base(0x5c18000000000000UL) | ((ulong)bReg << 8) | (250UL << 20) | (16UL << 39));
                        }

                        break;
                    }
                    case 0x307: // FRND.F32, round to an integral floating-point value
                    {
                        if (insn.Bits(75, 2) != 2 || insn.Bits(84, 2) != 2 || insn.Bits(81, 3) != 0 ||
                            insn.Bits(86, 6) != 0)
                        {
                            return Fail(index, "native floating round format");
                        }

                        Emit(Base(0x5ca8000000000000UL) | (2UL << 8) | (2UL << 10) |
                             ((ulong)insn.Bits(32, 8) << 20) | ((ulong)(8 + insn.Bits(78, 2)) << 39) |
                             ((ulong)insn.Bits(80, 1) << 44) | ((ulong)insn.Bits(63, 1) << 45) |
                             ((ulong)insn.Bits(62, 1) << 49));
                        break;
                    }
                    case 0x321: // ALD
                    case 0x322: // AST
                    {
                        if (insn.Bits(76, 2) != 0 || insn.Bits(79, 1) != 0)
                        {
                            return Fail(index, "native indexed/patch attribute access");
                        }

                        uint address = insn.Bits(40, 10);
                        uint components = insn.Bits(74, 2) + 1;

                        if (address % 4 != 0 || address + components * 4 > 0x400 || insn.Bits(24, 8) != 255)
                        {
                            return Fail(index, "native attribute address or indexing");
                        }

                        // a[0x5c] has no Maxwell meaning (Ampere uses it for a per-primitive hint such as the
                        // shading rate). Nothing reads it back; dropping the store keeps full-rate shading.
                        if (opcode == 0x322 && address == 0x5c && components == 1)
                        {
                            break;
                        }

                        uint valueReg = opcode == 0x321 ? dest : insn.Bits(32, 8);

                        if (valueReg == 255)
                        {
                            if (opcode == 0x321)
                            {
                                break; // load into RZ: nothing to do
                            }

                            // AST of RZ writes zeros: store them from zeroed scratch registers.
                            for (uint i = 0; i < components; ++i)
                            {
                                MovImm(250 + i, 0);
                            }

                            valueReg = 250;
                        }

                        if (valueReg + components > 255)
                        {
                            return Fail(index, "native attribute register range");
                        }

                        uint vertex = opcode == 0x321 ? insn.Bits(32, 8) : insn.Bits(64, 8);

                        result.Instructions.Add((opcode == 0x321 ? 0xefd8000000000000UL : 0xeff0000000000000UL) |
                             predicate | valueReg | (255UL << 8) | ((ulong)address << 20) |
                             ((ulong)vertex << 39) | ((ulong)(components - 1) << 47));
                        break;
                    }
                    case 0x387:
                    case 0x983: // Captured scalar local spill at an immediate byte offset.
                    {
                        bool store = opcode == 0x387;
                        uint data = insn.Bits(32, 8), offset = insn.Bits(40, 24);

                        if (insn.Bits(24, 8) != 255 || (store && (insn.Bits(16, 8) != 0 || data > 247)) ||
                            (!store && (data != 0 || dest >= 248)) || (offset & 3) != 0 ||
                            offset > 0x7fffff || insn.W2 != (store ? 0x00100800U : 0x00300800U))
                        {
                            return Fail(index, "native local scalar spill mode");
                        }

                        Emit((store ? 0xef50000000000000UL : 0xef40000000000000UL) | predicate |
                             (store ? data : dest) | (255UL << 8) | ((ulong)offset << 20) | (4UL << 48));
                        break;
                    }
                    case 0x803: // P2R PR, register merge source, immediate mask, byte select (76-77)
                    {
                        if (insn.W1 > 0x7f || (insn.W2 & ~0x3000U) != 0)
                        {
                            return Fail(index, "native predicate-to-register mode");
                        }

                        Emit(Base(0x38e8000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)insn.W1 << 20) | ((ulong)insn.Bits(76, 2) << 41));
                        break;
                    }
                    case 0x804: // R2P PR, register, immediate mask, byte select (76-77)
                    {
                        if (insn.Bits(16, 8) != 0 || insn.W1 > 0x7f || (insn.W2 & ~0x3000U) != 0)
                        {
                            return Fail(index, "native register-to-predicate mode");
                        }

                        Emit(0x38f0000000000000UL | predicate | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)insn.W1 << 20) | ((ulong)insn.Bits(76, 2) << 41));
                        break;
                    }
                    case 0xc02: // MOV uniform register
                    case 0x202: // MOV register
                    case 0x802: // MOV immediate
                    case 0xa02: // MOV constant buffer
                    {
                        if (insn.Bits(72, 4) != 15 ||
                            (insn.Bits(76, 16) & ~(opcode == 0xc02 ? 0x8000U : 0U)) != 0)
                        {
                            return Fail(index, "native MOV modifiers");
                        }

                        if (opcode == 0x802)
                        {
                            result.Instructions.Add(Base(0x0100000000000000UL) | (15UL << 12) |
                                ((ulong)insn.W1 << 20));
                        }
                        else if (opcode == 0xc02)
                        {
                            if (insn.W1 >= 16 || insn.W2 != 0x08000f00)
                            {
                                return Fail(index, "native uniform move source");
                            }

                            Emit(Base(0x5c98000000000000UL) | (15UL << 39) | ((ulong)(insn.W1 + 216) << 20));
                        }
                        else if (opcode == 0x202)
                        {
                            if (insn.Bits(40, 24) != 0)
                            {
                                return Fail(index, "native MOV register modifiers");
                            }

                            result.Instructions.Add(Base(0x5c98000000000000UL) | (15UL << 39) |
                                ((ulong)insn.Bits(32, 8) << 20));
                        }
                        else
                        {
                            if (!ValidCbuf() || insn.Bits(62, 2) != 0)
                            {
                                return Fail(index, "native MOV constant buffer");
                            }

                            result.Instructions.Add(Base(0x4c98000000000000UL) | (15UL << 39) | Cbuf());
                        }

                        break;
                    }
                    case 0x223:
                    case 0x423:
                    case 0x623:
                    case 0x823:
                    case 0xa23:
                    case 0xc23:
                    case 0xe23: // FFMA
                    {
                        if (insn.Bits(76, 1) != 0 ||
                            (insn.Bits(81, 11) & ~(opcode == 0xe23 || opcode == 0xc23 ? 0x400U : 0U)) != 0)
                        {
                            return Fail(index, "native FFMA denormal/source mode");
                        }

                        uint a = Operand(0), b = Operand(1), c = Operand(2);

                        Emit(Base(0x5980000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)c << 39) | ((ulong)insn.Bits(77, 1) << 50) |
                             ((ulong)insn.Bits(78, 2) << 51) | ((ulong)insn.Bits(80, 1) << 53));
                        break;
                    }
                    case 0x220:
                    case 0x820:
                    case 0xa20:
                    case 0xc20: // FMUL
                    {
                        uint scale = insn.Bits(84, 3);

                        if (insn.Bits(76, 1) != 0 || scale == 0 || insn.Bits(81, 3) != 0 ||
                            insn.Bits(87, 4) != 0 ||
                            insn.Bits(91, 1) != (opcode == 0xc20 ? 1U : 0U) ||
                            (scale != 4 && (insn.Bits(80, 1) == 0 || insn.Bits(78, 2) != 0)))
                        {
                            return Fail(index, "native FMUL scale/mode");
                        }

                        uint a = Operand(0), b = Operand(1);
                        uint[] fmulScale = [0, 3, 2, 1, 0, 6, 5, 4];

                        Emit(Base(0x5c68000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)insn.Bits(78, 2) << 39) | ((ulong)fmulScale[scale] << 41) |
                             ((ulong)insn.Bits(80, 1) << 44) | ((ulong)insn.Bits(77, 1) << 50));
                        break;
                    }
                    case 0x221:
                    case 0x421:
                    case 0x621:
                    case 0x821:
                    case 0xa21:
                    case 0xe21: // FADD
                    {
                        if (insn.Bits(81, 10) != 0 ||
                            insn.Bits(91, 1) != (opcode == 0xe21 ? 1U : 0U))
                        {
                            return Fail(index, "native FADD mode");
                        }

                        uint form = insn.Bits(9, 3);
                        uint a = Operand(0);
                        uint b = Operand(form == 2 || form == 3 || form == 7 ? 2U : 1U);

                        Emit(Base(0x5c58000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)insn.Bits(78, 2) << 39) | ((ulong)insn.Bits(80, 1) << 44) |
                             ((ulong)insn.Bits(77, 1) << 50));
                        break;
                    }
                    case 0x209:
                    case 0x809:
                    case 0xa09:
                    case 0xc09: // FMNMX, including a uniform source
                    {
                        if (insn.Bits(74, 6) != 0 || insn.Bits(81, 6) != 0 ||
                            (opcode != 0xc09 && insn.Bits(91, 1) != 0))
                        {
                            return Fail(index, "native floating min/max mode");
                        }

                        uint a = Operand(0), b = Operand(1);

                        Emit(Base(0x5c60000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)insn.Bits(87, 3) << 39) | ((ulong)insn.Bits(90, 1) << 42) |
                             ((ulong)insn.Bits(80, 1) << 44));
                        break;
                    }
                    case 0x308:
                    case 0xb08: // MUFU
                    {
                        uint operation = insn.Bits(74, 6);

                        if (operation > 8 || operation == 6 || operation == 7 || insn.Bits(80, 12) != 0)
                        {
                            return Fail(index, "native MUFU mode");
                        }

                        // SM70+ MUFU.COS/SIN take the angle in turns (compilers multiply by 1/2pi first);
                        // Maxwell MUFU takes radians (its RRO is a plain move), so scale by 2pi.
                        bool turns = operation == 0 || operation == 1;

                        uint ToRadians(uint source)
                        {
                            Emit(0x1e00000000000000UL | predicate | 250 | ((ulong)source << 8) |
                                 (0x40c90fdbUL << 20));
                            return 250;
                        }

                        if (insn.Bits(73, 1) != 0)
                        {
                            if (opcode != 0x308 || insn.Bits(60, 2) > 1)
                            {
                                return Fail(index, "native half special-function source");
                            }

                            Emit(0x5ca8000000000000UL | predicate | 250 | (2UL << 8) | (1UL << 10) |
                                 ((ulong)insn.Bits(32, 8) << 20) | ((ulong)insn.Bits(60, 1) << 41) |
                                 ((ulong)insn.Bits(63, 1) << 45) | ((ulong)insn.Bits(62, 1) << 49));

                            if (turns)
                            {
                                ToRadians(250);
                            }

                            Emit(0x5080000000000000UL | predicate | 250 | (250UL << 8) | ((ulong)operation << 20));
                            Emit(Base(0x5ca8000000000000UL) | (1UL << 8) | (2UL << 10) | (250UL << 20));
                        }
                        else
                        {
                            uint src = Operand(1);

                            if (turns)
                            {
                                src = ToRadians(src);
                            }

                            Emit(Base(0x5080000000000000UL) | ((ulong)src << 8) | ((ulong)operation << 20));
                        }

                        break;
                    }
                    case 0x526: // IPA.PASS / CONSTANT with the attribute indexed by a uniform register
                    {
                        uint indexRegister = UniformRegister(insn.Bits(24, 8));

                        if (indexRegister == uint.MaxValue || insn.Bits(32, 8) != 255 ||
                            (insn.Bits(76, 4) != 0 && insn.Bits(76, 4) != 4) || insn.Bits(81, 3) != 7)
                        {
                            return Fail(index, "native indexed IPA mode");
                        }

                        if (indexRegister == 255)
                        {
                            Emit(Base(0xe000000000000000UL) | (255UL << 8) | (255UL << 20) |
                                 ((ulong)insn.Bits(64, 8) << 30) | ((ulong)insn.Bits(78, 1) << 55));
                            break;
                        }

                        // Maxwell IPA.IDX reads the whole byte address from the index register.
                        Emit(0x1c00000000000000UL | predicate | 250 | ((ulong)indexRegister << 8) |
                             ((ulong)(insn.Bits(64, 8) * 4) << 20));
                        Emit(Base(0xe000000000000000UL) | (250UL << 8) | (255UL << 20) |
                             ((ulong)insn.Bits(64, 8) << 30) | (1UL << 38) | ((ulong)insn.Bits(78, 1) << 55));
                        break;
                    }
                    case 0x326: // IPA.PASS / CONSTANT, default or centroid sample location
                    {
                        // Bits 76-77 are the sample mode (title 1 interpolates most varyings at the centroid),
                        // bit 78 the constant mode; Maxwell keeps them at bits 52-53 and 55.
                        if (insn.Bits(32, 8) != 255 || insn.Bits(76, 2) > 1 || insn.Bits(79, 1) != 0 ||
                            insn.Bits(81, 3) != 7)
                        {
                            return Fail(index, "native IPA interpolation mode");
                        }

                        Emit(Base(0xe000000000000000UL) | (255UL << 8) | (255UL << 20) |
                             ((ulong)insn.Bits(64, 8) << 30) | ((ulong)insn.Bits(76, 2) << 52) |
                             ((ulong)insn.Bits(78, 1) << 55));
                        break;
                    }
                    case 0x81a: // SGXT.U32 Rd, Ra, n: keep the low n bits (BFE.U32 at position 0)
                    {
                        if (insn.W2 != 0 || insn.W1 > 32 || dest >= 255)
                        {
                            return Fail(index, "native sign extend mode");
                        }

                        Emit(Base(0x3800000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)(insn.W1 << 8) << 20));
                        break;
                    }
                    case 0x299: // USHF by a uniform amount, one register shifted (SHL/SHR, bit 75 wraps).
                    {
                        uint dst = UniformRegister(insn.Bits(16, 8)), low = UniformRegister(insn.Bits(24, 8));
                        uint shift = UniformRegister(insn.Bits(32, 8)), high = UniformRegister(insn.Bits(64, 8));
                        bool right = insn.Bits(76, 1) != 0, highResult = insn.Bits(80, 1) != 0;

                        if (dst == uint.MaxValue || dst == 255 || low == uint.MaxValue ||
                            shift == uint.MaxValue || high == uint.MaxValue || nativePredicate != 7 ||
                            insn.Bits(73, 2) != 3 || (insn.W2 & ~0x000118ffU) != 0x08000600U)
                        {
                            return Fail(index, "native uniform register shift mode");
                        }

                        uint source;

                        if (highResult && low == 255)
                        {
                            source = high; // (high:0) >> or << s, high word
                        }
                        else if (!highResult && high == 255)
                        {
                            source = low; // (0:low) >> or << s, low word
                        }
                        else
                        {
                            return Fail(index, "native uniform funnel shift");
                        }

                        Emit((right ? 0x5c28000000000000UL : 0x5c48000000000000UL) | predicate | dst |
                             ((ulong)source << 8) | ((ulong)shift << 20) | ((ulong)insn.Bits(75, 1) << 39));
                        break;
                    }
                    case 0x419: // SHF.L.W.U32.HI Rd, RZ, Rb, imm (high word of imm:0 << Rb) = imm << (Rb & 31)
                    {
                        if (insn.Bits(24, 8) != 255 || (insn.W2 & ~0xffU) != 0x00010e00)
                        {
                            return Fail(index, "native funnel shift immediate mode");
                        }

                        MovImm(250, insn.W1);
                        Emit(Base(0x5c48000000000000UL) | (250UL << 8) | ((ulong)insn.Bits(64, 8) << 20) |
                             (1UL << 39));
                        break;
                    }
                    case 0x805: // CS2R SRZ
                    {
                        if (insn.Bits(72, 8) != 255 || insn.Bits(80, 1) == 0 || dest >= 249)
                        {
                            return Fail(index, "native special-register read");
                        }

                        MovImm(dest, 0);
                        MovImm(dest + 1, 0);
                        break;
                    }
                    case 0x306:
                    case 0xd06: // I2F.F32.U8, low byte, round-to-nearest (0xd06: uniform source)
                    {
                        uint source = opcode == 0xd06 ? UniformRegister(insn.Bits(32, 8)) : insn.Bits(32, 8);

                        if (opcode == 0x306 && insn.W2 == 0x00101000 && (insn.W1 & 0xcfffff00U) == 0)
                        {
                            // I2F.U16 Rd, Rs(.H1): Maxwell's 16-bit source format, byte selector 0 or 2.
                            Emit(Base(0x5cb8000000000000UL) | (2UL << 8) | (1UL << 10) |
                                 ((ulong)source << 20) | ((ulong)(insn.Bits(60, 2) * 2) << 41));
                            break;
                        }

                        if (insn.W2 != (opcode == 0xd06 ? 0x08001000U : 0x1000U) ||
                            (insn.W1 & 0xffffff00U) != 0 || source == uint.MaxValue)
                        {
                            return Fail(index, "native byte-to-float conversion mode");
                        }

                        Emit(Base(0x5cb8000000000000UL) | (2UL << 8) | ((ulong)source << 20));
                        break;
                    }
                    case 0x245:
                    case 0xa45:
                    case 0xc45: // I2FP.F32.U32
                    {
                        // Bits 78-79: rounding (RN/RM/RP/RZ), as Maxwell I2F's bits 39-40.
                        if ((insn.W2 & ~(0x400U | 0xc000U | (opcode == 0xc45 ? 0x08000000U : 0U))) != 0x00201000)
                        {
                            return Fail(index, "native packed integer-to-float format");
                        }

                        uint src = Operand(1, false);

                        Emit(Base(0x5cb8000000000000UL) | (2UL << 8) | (2UL << 10) |
                             ((ulong)insn.Bits(74, 1) << 13) | ((ulong)src << 20) |
                             ((ulong)insn.Bits(78, 2) << 39));
                        break;
                    }
                    case 0x207:
                    case 0x807:
                    case 0xa07:
                    case 0x208:
                    case 0x808:
                    case 0xa08: // Bitwise SEL / FSEL
                    {
                        uint a = Operand(0, false), b = Operand(1, false);

                        Emit(Base(0x5ca0000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)insn.Bits(87, 3) << 39) | ((ulong)insn.Bits(90, 1) << 42));
                        break;
                    }
                    case 0x20a:
                    case 0x80a:
                    case 0xa0a: // FSET.BF, combined with Pc (bits 87-90) by bits 74-75
                    {
                        if (insn.Bits(64, 8) != 0 || insn.Bits(74, 2) == 3 || insn.Bits(81, 6) != 0 ||
                            insn.Bits(91, 1) != 0)
                        {
                            return Fail(index, "native floating comparison output mode");
                        }

                        uint a = Operand(0), b = Operand(1);

                        Emit(Base(0x5800000000000000UL) | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)insn.Bits(87, 4) << 39) | ((ulong)insn.Bits(74, 2) << 45) |
                             ((ulong)insn.Bits(76, 4) << 48) | (1UL << 52) | ((ulong)insn.Bits(80, 1) << 55));
                        break;
                    }
                    case 0x434: // HSETP2 with an immediate half pair: A swizzle 74-75 (neg 72, abs 73),
                    {           // combine 69-70, H_AND 71, compare 76-79, FTZ 80, P/Q 81-86, input 87-90.
                        MovImm(250, insn.W1);
                        Emit(0x5d20000000000000UL | predicate | insn.Bits(84, 3) |
                             ((ulong)insn.Bits(81, 3) << 3) | ((ulong)insn.Bits(80, 1) << 6) |
                             ((ulong)insn.Bits(24, 8) << 8) | (250UL << 20) |
                             ((ulong)insn.Bits(76, 4) << 35) | ((ulong)insn.Bits(87, 3) << 39) |
                             ((ulong)insn.Bits(90, 1) << 42) | ((ulong)insn.Bits(72, 1) << 43) |
                             ((ulong)insn.Bits(73, 1) << 44) | ((ulong)insn.Bits(69, 2) << 45) |
                             ((ulong)insn.Bits(74, 2) << 47) | ((ulong)insn.Bits(71, 1) << 49));
                        break;
                    }
                    case 0x20b:
                    case 0x80b:
                    case 0xa0b: // FSETP
                    case 0x20c:
                    case 0x80c:
                    case 0xa0c:
                    case 0xc0c: // ISETP
                    {
                        bool floating = (opcode & 0x1ff) == 0xb;

                        if (!floating && insn.Bits(72, 1) != 0)
                        {
                            return Fail(index, "native extended integer comparison");
                        }

                        uint a = Operand(0, floating), b = Operand(1, floating);
                        uint combine = insn.Bits(74, 2);

                        if (combine > 2)
                        {
                            return Fail(index, "native predicate combination");
                        }

                        ulong output = (floating ? 0x5bb0000000000000UL : 0x5b60000000000000UL) | predicate |
                             insn.Bits(84, 3) | ((ulong)insn.Bits(81, 3) << 3) | ((ulong)a << 8) |
                             ((ulong)b << 20) | ((ulong)insn.Bits(87, 3) << 39) |
                             ((ulong)insn.Bits(90, 1) << 42) | ((ulong)combine << 45);

                        if (floating)
                        {
                            output |= ((ulong)insn.Bits(80, 1) << 47) | ((ulong)insn.Bits(76, 4) << 48);
                        }
                        else
                        {
                            output |= ((ulong)insn.Bits(73, 1) << 48) | ((ulong)insn.Bits(76, 3) << 49);
                        }

                        Emit(output);
                        break;
                    }
                    case 0x212:
                    case 0x812:
                    case 0xa12:
                    case 0xc12: // LOP3.LUT, any table, optional P = (result != 0)
                    {
                        uint lut = insn.Bits(72, 8);
                        uint predTarget = insn.Bits(81, 3);

                        if (insn.Bits(80, 1) != 0 || insn.Bits(87, 4) != 15 ||
                            (opcode == 0x212 && insn.Bits(40, 24) != 0) ||
                            (opcode == 0xa12 && insn.Bits(62, 2) != 0))
                        {
                            return Fail(index, "native LUT/predicate mode");
                        }

                        uint b = 255;

                        if (opcode == 0x212)
                        {
                            b = insn.Bits(32, 8);
                        }
                        else if (opcode == 0x812)
                        {
                            MovImm(251, insn.W1);
                            b = 251;
                        }
                        else if (opcode == 0xc12)
                        {
                            b = UniformRegister(insn.Bits(32, 8));

                            if (b == uint.MaxValue)
                            {
                                return Fail(index, "native uniform logic source");
                            }
                        }
                        else
                        {
                            b = Operand(1, false);
                        }

                        uint a = insn.Bits(24, 8), c = insn.Bits(64, 8);

                        Emit(0x5be0000000000000UL | predicate | dest | ((ulong)a << 8) | ((ulong)b << 20) |
                             ((ulong)lut << 28) | ((ulong)(predTarget == 7 ? 0U : 3U) << 36) |
                             ((ulong)c << 39) | ((ulong)predTarget << 48));
                        break;
                    }
                    case 0x213:
                    case 0xa13:
                    case 0xc13: // IABS: max(x, 0 - x), signed.
                    {
                        if ((insn.W2 & ~(opcode == 0xc13 ? 0x08000000U : 0U)) != 0)
                        {
                            return Fail(index, "native integer absolute mode");
                        }

                        uint source = Operand(1, false);

                        Emit(0x5c10000000000000UL | predicate | 250 | (255UL << 8) | ((ulong)source << 20) |
                             (1UL << 48));
                        Emit(Base(0x5c20000000000000UL) | ((ulong)source << 8) | (250UL << 20) |
                             (15UL << 39) | (1UL << 48));
                        break;
                    }
                    case 0x217:
                    case 0x817:
                    case 0xa17:
                    case 0xc17: // IMNMX register, immediate, cbuf or uniform operand.
                    {
                        if ((insn.W2 & ~(0x04000200U | (opcode == 0xc17 ? 0x08000000U : 0U))) != 0x03800000 ||
                            (opcode != 0xa17 && insn.W1 > (opcode == 0x817 ? 0x7ffffU : 255U)) ||
                            (opcode == 0xa17 && (!ValidCbuf() || insn.Bits(62, 2) != 0)))
                        {
                            return Fail(index, "native integer min/max mode");
                        }

                        uint source = opcode == 0xc17 ? UniformRegister(insn.W1)
                                     : opcode == 0xa17 ? Operand(1, false)
                                                       : insn.W1;

                        if (source == uint.MaxValue)
                        {
                            return Fail(index, "native uniform min/max operand");
                        }

                        Emit(Base(opcode == 0x817 ? 0x3820000000000000UL : 0x5c20000000000000UL) |
                             ((ulong)insn.Bits(24, 8) << 8) | ((ulong)source << 20) |
                             ((ulong)insn.Bits(87, 4) << 39) | ((ulong)insn.Bits(73, 1) << 48));
                        break;
                    }
                    case 0x819: // SHF.HI, immediate shift
                    {
                        bool isSigned = insn.Bits(73, 2) == 2;

                        if (insn.Bits(80, 1) == 0 && insn.Bits(76, 1) == 0 && insn.Bits(64, 8) == 255 &&
                            insn.Bits(73, 2) == 3 && insn.Bits(75, 1) == 0 && insn.W1 <= 32)
                        {
                            Emit(Base(0x3848000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)insn.W1 << 20));
                            break;
                        }

                        if (insn.Bits(80, 1) == 0 && insn.Bits(76, 1) != 0 && insn.Bits(64, 8) == 255 &&
                            (insn.Bits(73, 2) == 3 || isSigned) && insn.Bits(75, 1) == 0 && insn.W1 <= 32)
                        {
                            // SHF.R low word with a zero high word: a logical right shift.
                            Emit(Base(0x3828000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)insn.W1 << 20));
                            break;
                        }

                        if ((insn.Bits(73, 2) != 3 && !isSigned) || insn.Bits(75, 1) != 0 ||
                            insn.Bits(80, 1) == 0 || insn.W1 > 32)
                        {
                            return Fail(index, "native funnel-shift mode");
                        }

                        uint low = insn.Bits(24, 8), high = insn.Bits(64, 8), shift = insn.W1;

                        if (insn.Bits(76, 1) != 0)
                        {
                            if (low != 255)
                            {
                                return Fail(index, "native nonzero low right-shift word");
                            }

                            Emit(Base(0x3828000000000000UL) | ((ulong)high << 8) | ((ulong)shift << 20) |
                                 ((ulong)(isSigned ? 1U : 0U) << 48));
                        }
                        else
                        {
                            if (isSigned)
                            {
                                return Fail(index, "native signed left funnel shift");
                            }

                            Emit(Base(0x36f8000000000000UL) | ((ulong)low << 8) | ((ulong)high << 39) |
                                 ((ulong)shift << 20));
                        }

                        break;
                    }
                    case 0xa11: // LEA with constant base and carry predicate.
                    {
                        if (insn.Bits(72, 1) != 0 || insn.Bits(74, 1) != 0 || insn.Bits(80, 1) != 0 ||
                            insn.Bits(81, 3) == 7 || insn.Bits(62, 2) != 0)
                        {
                            return Fail(index, "native constant LEA carry mode");
                        }

                        uint b = Operand(1, false);

                        Emit(Base(0x5c18000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)b << 20) | ((ulong)insn.Bits(75, 5) << 39));
                        Emit(0x5b60000000000000UL | predicate | 7 | ((ulong)insn.Bits(81, 3) << 3) |
                             (dest << 8) | ((ulong)b << 20) | (7UL << 39) | (1UL << 49));
                        break;
                    }
                    case 0x211:
                    case 0x811: // LEA low word, no carry result
                    {
                        if (insn.Bits(80, 1) != 0)
                        {
                            // LEA.HI Rd, Ra, Rb (or an immediate, 0x811), RZ, s: Rb + high word of (Ra << s);
                            // bit 73 sign-extends Ra.
                            if (insn.Bits(72, 1) != 0 || insn.Bits(74, 1) != 0 || insn.Bits(81, 3) != 7 ||
                                insn.Bits(64, 8) != 255 || (opcode == 0x211 && insn.Bits(62, 2) != 0))
                            {
                                return Fail(index, "native high effective address mode");
                            }

                            uint b = insn.Bits(32, 8);

                            if (opcode == 0x811)
                            {
                                b = insn.W1 != 0 ? 251U : 255U;

                                if (b == 251)
                                {
                                    MovImm(251, insn.W1);
                                }
                            }

                            ulong shift = ((ulong)(32 - insn.Bits(75, 5)) << 20) | ((ulong)insn.Bits(73, 1) << 48);

                            if (b == 255)
                            {
                                Emit(Base(0x3828000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) | shift);
                            }
                            else
                            {
                                Emit(0x3828000000000000UL | predicate | 250 | ((ulong)insn.Bits(24, 8) << 8) | shift);
                                Emit(Base(0x5c10000000000000UL) | (250UL << 8) | ((ulong)b << 20));
                            }

                            break;
                        }

                        if (insn.Bits(72, 1) != 0 || insn.Bits(74, 1) != 0 || insn.Bits(80, 1) != 0 ||
                            insn.Bits(81, 3) != 7 || (opcode == 0x211 && insn.Bits(62, 1) != 0))
                        {
                            return Fail(index, "native LEA carry/high mode");
                        }

                        // 0x211 bit 63 negates Rb (LEA Rd, Ra, -Rb, s), as Maxwell ISCADD's bit 48.
                        uint bReg = insn.Bits(32, 8);

                        if (opcode == 0x811)
                        {
                            MovImm(251, insn.W1);
                            bReg = 251;
                        }

                        Emit(Base(0x5c18000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                             ((ulong)bReg << 20) | ((ulong)insn.Bits(75, 5) << 39) |
                             ((ulong)(opcode == 0x211 ? insn.Bits(63, 1) : 0U) << 48));
                        break;
                    }
                    case 0x210:
                    case 0x810:
                    case 0xa10:
                    case 0xc10: // IADD3 with zero third source and no carry outputs
                    {
                        // 64-bit adds: IADD3 with a carry-out predicate (bits 81-83), then IADD3.X (bit 74) with
                        // a carry-in predicate (bits 87-89, negate 90). Uniform forms keep UPn in R239 bit 6-n.
                        if ((opcode == 0x210 || opcode == 0x810) && insn.Bits(64, 8) == 255 &&
                            insn.Bits(73, 1) == 0 && insn.Bits(75, 2) == 0 && insn.Bits(84, 3) == 7 &&
                            insn.Bits(77, 4) == 15 && insn.Bits(72, 1) == 0 &&
                            (insn.Bits(81, 3) != 7 || insn.Bits(74, 1) != 0))
                        {
                            bool carryIn = insn.Bits(74, 1) != 0;
                            uint carryOut = insn.Bits(81, 3);
                            uint a = insn.Bits(24, 8);

                            if ((carryIn && carryOut != 7) ||
                                (opcode == 0x210 && (insn.W1 & ~0xffU) != 0) ||
                                (rewrittenUniform && carryOut != 7 && carryOut > 1) ||
                                (rewrittenUniform && carryIn && insn.Bits(87, 3) > 1 && insn.Bits(87, 3) != 7))
                            {
                                return Fail(index, "native integer carry mode");
                            }

                            if (opcode == 0x810)
                            {
                                Emit(0x1c00000000000000UL | predicate | 250 | ((ulong)a << 8) |
                                     ((ulong)insn.W1 << 20));
                            }
                            else
                            {
                                Emit(0x5c10000000000000UL | predicate | 250 | ((ulong)a << 8) |
                                     ((ulong)insn.Bits(32, 8) << 20));
                            }

                            void SaveCarry(uint mask)
                            {
                                Emit(0x38e8000000070000UL | 253 | (255UL << 8) | ((ulong)mask << 20));
                            }

                            void RestoreCarry(uint mask)
                            {
                                Emit(0x38f0000000070000UL | (253UL << 8) | ((ulong)mask << 20));
                            }

                            if (!carryIn)
                            {
                                // carry = (a + b) < a, unsigned
                                uint target = rewrittenUniform ? 6 - carryOut : carryOut;
                                uint mask = 1U << (int)target;

                                if (rewrittenUniform)
                                {
                                    SaveCarry(mask);
                                }

                                Emit(0x5b60000000000000UL | predicate | 7 | ((ulong)target << 3) |
                                     (250UL << 8) | ((ulong)a << 20) | (7UL << 39) | (1UL << 49));

                                if (rewrittenUniform)
                                {
                                    Emit(0x38e8000000070000UL | 239 | (239UL << 8) | ((ulong)mask << 20));
                                    RestoreCarry(mask);
                                }

                                Emit(Base(0x5c98000000000000UL) | (15UL << 39) | (250UL << 20));
                                break;
                            }

                            uint condition = insn.Bits(87, 3);
                            uint source = rewrittenUniform && condition != 7 ? 6 - condition : condition;
                            uint sourceMask = 1U << (int)source;

                            MovImm(252, 1);

                            if (rewrittenUniform && condition != 7)
                            {
                                SaveCarry(sourceMask);
                                Emit(0x38f0000000070000UL | (239UL << 8) | ((ulong)sourceMask << 20));
                            }

                            Emit(0x5ca0000000000000UL | predicate | 251 | (252UL << 8) | (255UL << 20) |
                                 ((ulong)source << 39) | ((ulong)insn.Bits(90, 1) << 42));

                            if (rewrittenUniform && condition != 7)
                            {
                                RestoreCarry(sourceMask);
                            }

                            Emit(Base(0x5c10000000000000UL) | (250UL << 8) | (251UL << 20));
                            break;
                        }

                        if (opcode == 0xa10 && insn.Bits(81, 3) != 7)
                        {
                            if (insn.Bits(64, 8) != 255 || insn.Bits(73, 4) != 0 || insn.Bits(84, 7) != 0x7f ||
                                insn.Bits(77, 4) != 15 || insn.Bits(63, 1) != 0 || insn.Bits(72, 1) != 0)
                            {
                                return Fail(index, "native constant add carry mode");
                            }

                            uint b = Operand(1, false);

                            Emit(Base(0x5c10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) | ((ulong)b << 20));
                            Emit(0x5b60000000000000UL | predicate | 7 | ((ulong)insn.Bits(81, 3) << 3) |
                                 (dest << 8) | ((ulong)b << 20) | (7UL << 39) | (1UL << 49));
                            break;
                        }

                        if (opcode == 0xa10 && insn.Bits(74, 1) != 0)
                        {
                            if (insn.Bits(24, 8) != 255 || (insn.W2 & ~0x07800000U) != 0x007fe4ffU)
                            {
                                return Fail(index, "native extended constant add mode");
                            }

                            uint b = Operand(1, false);

                            MovImm(250, 1);
                            Emit(0x5ca0000000000000UL | predicate | 250 | (250UL << 8) | (255UL << 20) |
                                 ((ulong)insn.Bits(87, 3) << 39) | ((ulong)insn.Bits(90, 1) << 42));
                            Emit(Base(0x5c10000000000000UL) | ((ulong)b << 8) | (250UL << 20));
                            break;
                        }

                        // Third source, optionally negated (bit 75; 63 and 72 negate b and a).
                        if (insn.Bits(64, 8) != 255 && (insn.Bits(73, 4) & ~4U) == 0 &&
                            insn.Bits(77, 7) == 0x7f && insn.Bits(84, 7) == 0x7f)
                        {
                            uint b = Operand(1, false);

                            Emit(0x5c10000000000000UL | predicate | 250 | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)b << 20) | ((ulong)insn.Bits(63, 1) << 48) |
                                 ((ulong)insn.Bits(72, 1) << 49));
                            Emit(Base(0x5c10000000000000UL) | (250UL << 8) | ((ulong)insn.Bits(64, 8) << 20) |
                                 ((ulong)insn.Bits(75, 1) << 48));
                            break;
                        }

                        if (insn.Bits(64, 8) != 255 || insn.Bits(73, 4) != 0 ||
                            insn.Bits(77, 7) != 0x7f || insn.Bits(84, 7) != 0x7f)
                        {
                            return Fail(index, "native integer carry/third source");
                        }

                        if (opcode == 0xa10 || opcode == 0xc10)
                        {
                            if (opcode == 0xc10 && (insn.W1 & ~0x8000003fU) != 0)
                            {
                                return Fail(index, "native uniform add operand");
                            }

                            uint b = Operand(1, false);

                            Emit(Base(0x5c10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) | ((ulong)b << 20) |
                                 ((ulong)insn.Bits(63, 1) << 48) | ((ulong)insn.Bits(72, 1) << 49));
                        }
                        else if (opcode == 0x210)
                        {
                            if ((insn.W1 & ~0x800000ffU) != 0)
                            {
                                return Fail(index, "native integer add register modifiers");
                            }

                            Emit(Base(0x5c10000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)insn.Bits(32, 8) << 20) | ((ulong)insn.Bits(63, 1) << 48) |
                                 ((ulong)insn.Bits(72, 1) << 49));
                        }
                        else
                        {
                            Emit(Base(0x1c00000000000000UL) | ((ulong)insn.Bits(24, 8) << 8) |
                                 ((ulong)insn.W1 << 20) | ((ulong)insn.Bits(72, 1) << 56));
                        }

                        break;
                    }
                    case 0x949:
                    case 0x958: // Captured BRX / BRXU with absolute native-PC table values
                    {
                        if (insn.Bits(12, 4) != 7 ||
                            (insn.W2 & ~0x3ffffU) != (opcode == 0x958 ? 0x0b800000U : 0x03800000U))
                        {
                            return Fail(index, "native computed branch mode");
                        }

                        ulong raw = insn.W1 | ((ulong)(insn.W2 & 0x3ffff) << 32);
                        long relative = unchecked((long)(raw << 14)) >> 14;

                        if ((long)index * 16 + 16 + relative != 0)
                        {
                            return Fail(index, "native computed branch relative base");
                        }

                        long offset = -(long)AmpereLayout.AddressOf((uint)result.Instructions.Count) - 8;

                        if (offset < -0x800000)
                        {
                            return Fail(index, "native computed branch range");
                        }

                        uint source = opcode == 0x958 ? UniformRegister(insn.Bits(24, 8)) : insn.Bits(24, 8);

                        if (source == uint.MaxValue)
                        {
                            return Fail(index, "native uniform computed branch source");
                        }

                        Emit(0xe25000000000000fUL | predicate | ((ulong)source << 8) |
                             (((ulong)offset & 0xffffff) << 20));
                        computedBranch = true;
                        break;
                    }
                    case 0x944: // Verified CALL.REL.NOINC with R55 holding the return PC
                    {
                        // A predicated NOINC forward transfer can terminate a loop at its
                        // reconvergence point without a return-link setup or matching RET.
                        if (nativePredicate != 7 && insn.W1 == 16 && insn.W2 == 0x03c00000 &&
                            (int)index + 2 < code.Length && code[(int)index + 1].Bits(0, 12) == 0x947)
                        {
                            relocations.Add(new Relocation((uint)result.Instructions.Count, index + 2));
                            Emit(0xe24000000000000fUL | predicate);
                            break;
                        }

                        if (mainExited || index == 0 || insn.W0 != 0x7944 ||
                            (insn.W2 & ~0x3ffffU) != 0x03c00000)
                        {
                            return Fail(index, "native call linkage mode");
                        }

                        {
                            AmpereInstruction prior = code[(int)(index - 1)];

                            if ((prior.W0 & ~0x00ff0000U) != 0x00007802 || prior.Bits(16, 8) >= 250 ||
                                prior.W1 != (index + 1) * 16 || prior.W2 != 0xf00)
                            {
                                return Fail(index, "native call linkage mode");
                            }

                            ulong raw = insn.W1 | ((ulong)(insn.W2 & 0x3ffff) << 32);
                            long relative = unchecked((long)(raw << 14)) >> 14;
                            long target = (long)index * 16 + 16 + relative;

                            if (target < 0 || target % 16 != 0)
                            {
                                return Fail(index, "native call destination alignment");
                            }

                            uint link = prior.Bits(16, 8);

                            for (int call = 0; call < callTargets.Count; ++call)
                            {
                                if (callTargets[call] == target / 16 && callLinks[call] != link)
                                {
                                    return Fail(index, "native inconsistent call linkage");
                                }
                            }

                            callTargets.Add((uint)(target / 16));
                            callLinks.Add(link);
                            relocations.Add(new Relocation((uint)result.Instructions.Count, target / 16));
                            Emit(0xe26000000007000fUL);
                        }

                        break;
                    }
                    case 0x950: // Verified RET.REL.NODEC epilogue restoring the call link
                    {
                        uint returnReg = insn.Bits(24, 8);

                        // The return register is the link itself, or the last write to it in this function
                        // is a copy of the link (MOV or IMAD.MOV.U32 Rd, RZ, RZ, Rlink).
                        bool CopiesLink(AmpereInstruction prior)
                        {
                            if (prior.Bits(16, 8) != returnReg || prior.Bits(12, 4) != 7)
                            {
                                return false;
                            }

                            if ((prior.W0 & 0xff00ffffU) == 0x00007202U)
                            {
                                return prior.W1 == activeLink.Value && prior.W2 == 0xf00;
                            }

                            if ((prior.W0 & 0xff00ffffU) == 0xff007224U)
                            {
                                return prior.W1 == 0xff && prior.W2 == (0x078e0000U | activeLink.Value);
                            }

                            return false;
                        }

                        bool linked = activeLink.HasValue && returnReg == activeLink.Value;

                        for (uint prior = index; activeLink.HasValue && !linked && prior-- > activeEntry;)
                        {
                            if (CopiesLink(code[(int)prior]))
                            {
                                linked = true;
                            }
                            else if (code[(int)prior].Bits(16, 8) == returnReg)
                            {
                                break;
                            }
                        }

                        if (!mainExited || !linked || (insn.W0 & 0x00ffffffU) != 0x00007950 ||
                            returnReg >= 249 || (insn.W2 & ~0x3ffffU) != 0x03c00000)
                        {
                            return Fail(index, "native return linkage mode");
                        }

                        ulong raw = insn.W1 | ((ulong)(insn.W2 & 0x3ffff) << 32);
                        long relative = unchecked((long)(raw << 14)) >> 14;

                        if ((long)index * 16 + 16 + relative != 0)
                        {
                            return Fail(index, "native return relative base");
                        }

                        Emit(0xe32000000007000fUL);

                        bool complete = !computedBranch;

                        foreach (uint target in callTargets)
                        {
                            complete &= target <= index;
                        }

                        if (complete)
                        {
                            return Finish(index);
                        }

                        break;
                    }
                    case 0x945:
                    case 0x941:
                    case 0x942:
                    case 0x948: // BSSY / BSYNC / BREAK / WARPSYNC
                    {
                        // BSSY / BSYNC / BREAK only manage warp reconvergence: BSYNC waits for the other
                        // threads and falls through, BREAK drops the thread from that wait. Ryujing runs
                        // threads independently, so all are no-ops (a SYNC would wrongly jump, and a branch
                        // out of a lowered SSY region would leave its token on the flow stack).
                        break;
                    }
                    case 0x947: // BRA
                    {
                        ulong branchPredicate = predicate;

                        if (insn.Bits(87, 4) != 7)
                        {
                            if (insn.Bits(12, 4) != 7)
                            {
                                relocations.Add(new Relocation((uint)result.Instructions.Count, index + 1));
                                Emit(0xe24000000000000fUL | ((ulong)(insn.Bits(12, 4) ^ 8U) << 16));
                            }

                            branchPredicate = (ulong)insn.Bits(87, 4) << 16;
                        }

                        ulong raw = insn.W1 | ((ulong)(insn.W2 & 0x3ffff) << 32);
                        long relative = unchecked((long)(raw << 14)) >> 14;
                        long target = (long)index * 16 + 16 + relative;

                        if (target < 0 || target % 16 != 0)
                        {
                            return Fail(index, "native branch destination alignment");
                        }

                        relocations.Add(new Relocation((uint)result.Instructions.Count, target / 16));
                        Emit(0xe24000000000000fUL | branchPredicate);

                        if (computedBranch && target == (long)index * 16 && branchPredicate == (7UL << 16))
                        {
                            return Finish(index); // the closing self-branch
                        }

                        break;
                    }
                    case 0x94d: // EXIT
                    {
                        if (insn.Bits(84, 2) != 0)
                        {
                            return Fail(index, "native conditional/special EXIT");
                        }

                        if (insn.Bits(87, 4) != 7)
                        {
                            // @Pg EXIT Pc: exits when both hold; skip over a Pc-predicated EXIT unless Pg.
                            if (nativePredicate != 7)
                            {
                                relocations.Add(new Relocation((uint)result.Instructions.Count, index + 1));
                                Emit(0xe24000000000000fUL | ((ulong)(nativePredicate ^ 8U) << 16));
                            }

                            Emit(0xe30000000000000fUL | ((ulong)insn.Bits(87, 4) << 16));
                            break;
                        }

                        result.Instructions.Add(0xe30000000000000fUL | predicate);

                        if (insn.Bits(12, 4) != 7)
                        {
                            break;
                        }

                        if (callTargets.Count == 0)
                        {
                            bool pendingBranch = false;

                            foreach (Relocation relocation in relocations)
                            {
                                if (relocation.Target > index)
                                {
                                    pendingBranch = true;
                                    break;
                                }
                            }

                            if (!pendingBranch && !computedBranch)
                            {
                                return Finish(index);
                            }

                            break;
                        }

                        if (mainExited)
                        {
                            // A library function may end the shader instead of returning (Ryujing inlines
                            // calls, so this is an ordinary exit at that point).
                            bool complete = true;

                            foreach (Relocation relocation in relocations)
                            {
                                if (relocation.Target > index)
                                {
                                    complete = false;
                                    break;
                                }
                            }

                            foreach (uint target in callTargets)
                            {
                                complete &= target <= index;
                            }

                            if (complete && !computedBranch)
                            {
                                return Finish(index);
                            }

                            break;
                        }

                        foreach (uint target in callTargets)
                        {
                            if (target <= index)
                            {
                                return Fail(index, "native call target overlaps main program");
                            }
                        }

                        mainExited = true;
                        break;
                    }
                    case 0x23a:
                    {
                        return Fail(index, "native matrix move");
                    }
                    case 0x23c:
                    {
                        return Fail(index, "native matrix multiply");
                    }

                    // __MORE_CASES__

                    default:
                        return Fail(index, "unsupported native opcode");
                }

                if (!string.IsNullOrEmpty(operandError))
                {
                    return Fail(index, operandError);
                }

                if (outputMask != 0)
                {
                    Emit(0x38e8000000070000UL | 239 | (239UL << 8) | ((ulong)outputMask << 20));
                    Emit(0x38f0000000070000UL | (254UL << 8) | ((ulong)outputMask << 20));
                }

                if (guardedUniform)
                {
                    RestorePredicate(guardMask);
                }
            }

            return Fail((uint)code.Length, mainExited
                ? "native program lacks captured library return"
                : "native program has no unconditional EXIT");
        }
    }
}
