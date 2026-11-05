// SPDX-License-Identifier: MIT
using ARMeilleure.Decoders;
using ARMeilleure.Instructions;
using ARMeilleure.State;
using NUnit.Framework;

namespace Ryujinx.Tests.Cpu
{
    public class CpuTestSimdDotProd : CpuTest
    {
        // NOTE: every test below runs with runUnicorn: false and no Unicorn
        // comparison. The bundled Unicorn predates FEAT_DotProd: it raises
        // UC_ERR_EXCEPTION (or spins) on SDOT/UDOT instead of executing them.
        // Expected vectors are hand-computed; the tested encodings were
        // captured from aarch64-linux-gnu-as (see .nx2-logs/asm/dotprod.s).
        private static uint WithRegs(uint baseOpcode, uint rd, uint rn, uint rm)
        {
            baseOpcode &= ~((31u << 16) | (31u << 5) | 31u);

            return baseOpcode | ((rm & 31) << 16) | ((rn & 31) << 5) | (rd & 31);
        }

        [Test, Description("SDOT <Vd>.4S, <Vn>.16B, <Vm>.16B")]
        public void Sdot_V_4S([Values(0u)] uint rd,
                              [Values(1u)] uint rn,
                              [Values(2u)] uint rm)
        {
            // Each 32-bit lane accumulates four 1*2 products: 8 per lane.
            uint opcode = WithRegs(0x4E829420u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0ul, 0ul);
            V128 v1 = MakeVectorE0E1(0x0101010101010101ul, 0x0101010101010101ul);
            V128 v2 = MakeVectorE0E1(0x0202020202020202ul, 0x0202020202020202ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0x0000000800000008ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0x0000000800000008ul));
            });
        }

        [Test, Description("SDOT <Vd>.4S sign-extends 8-bit lanes")]
        public void Sdot_V_4S_Signed([Values(0u)] uint rd,
                                     [Values(1u)] uint rn,
                                     [Values(2u)] uint rm)
        {
            // 0xFF is -1 signed: four -1*2 products per lane give -8 (0xFFFFFFF8).
            // Zero extension would give 2040 (0x7F8) instead.
            uint opcode = WithRegs(0x4E829420u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0ul, 0ul);
            V128 v1 = MakeVectorE0E1(0xFFFFFFFFFFFFFFFFul, 0xFFFFFFFFFFFFFFFFul);
            V128 v2 = MakeVectorE0E1(0x0202020202020202ul, 0x0202020202020202ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0xFFFFFFF8FFFFFFF8ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0xFFFFFFF8FFFFFFF8ul));
            });
        }

        [Test, Description("UDOT <Vd>.4S, <Vn>.16B, <Vm>.16B")]
        public void Udot_V_4S([Values(0u)] uint rd,
                              [Values(1u)] uint rn,
                              [Values(2u)] uint rm)
        {
            // 0xFF is 255 unsigned: four 255*2 products per lane give 2040 (0x7F8).
            uint opcode = WithRegs(0x6E829420u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0ul, 0ul);
            V128 v1 = MakeVectorE0E1(0xFFFFFFFFFFFFFFFFul, 0xFFFFFFFFFFFFFFFFul);
            V128 v2 = MakeVectorE0E1(0x0202020202020202ul, 0x0202020202020202ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0x000007F8000007F8ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0x000007F8000007F8ul));
            });
        }

        [Test, Description("SDOT <Vd>.2S zeroes the upper 64 bits")]
        public void Sdot_V_2S_UpperZeroed([Values(0u)] uint rd,
                                          [Values(1u)] uint rn,
                                          [Values(2u)] uint rm)
        {
            // Upper lanes must not leak in: 0xFF/0x03 up there would give
            // four -1*3 products (-12) per upper lane if wrongly included.
            uint opcode = WithRegs(0x0E829420u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0ul, 0ul);
            V128 v1 = MakeVectorE0E1(0x0101010101010101ul, 0xFFFFFFFFFFFFFFFFul);
            V128 v2 = MakeVectorE0E1(0x0202020202020202ul, 0x0303030303030303ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0x0000000800000008ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0ul));
            });
        }

        [Test, Description("SDOT <Vd>.4S accumulates onto the destination lanes")]
        public void Sdot_V_AccumulatesRd([Values(0u)] uint rd,
                                         [Values(1u)] uint rn,
                                         [Values(2u)] uint rm)
        {
            // [1, 2, 3, 4] plus 8 per lane gives [9, 10, 11, 12].
            uint opcode = WithRegs(0x4E829420u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0x0000000200000001ul, 0x0000000400000003ul);
            V128 v1 = MakeVectorE0E1(0x0101010101010101ul, 0x0101010101010101ul);
            V128 v2 = MakeVectorE0E1(0x0202020202020202ul, 0x0202020202020202ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0x0000000A00000009ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0x0000000C0000000Bul));
            });
        }

        [Test, Description("SDOT <Vd>.4S, <Vn>.16B, <Vm>.4B[index]")]
        public void Sdot_Ve([Values(0u)] uint rd,
                            [Values(1u)] uint rn,
                            [Values(2u)] uint rm)
        {
            // Index 1 selects bytes 4..7 (4, 5, 6, 7): 4+5+6+7 = 22 per lane.
            uint opcode = WithRegs(0x4FA2E020u, rd, rn, rm);

            V128 v0 = MakeVectorE0E1(0ul, 0ul);
            V128 v1 = MakeVectorE0E1(0x0101010101010101ul, 0x0101010101010101ul);
            V128 v2 = MakeVectorE0E1(0x0706050403020100ul, 0x0F0E0D0C0B0A0908ul);

            ExecutionContext context = SingleOpcode(opcode, v0: v0, v1: v1, v2: v2, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(0)), Is.EqualTo(0x0000001600000016ul));
                Assert.That(GetVectorE1(context.GetV(0)), Is.EqualTo(0x0000001600000016ul));
            });
        }

        [Test, Description("UDOT <Vd>.4S, <Vn>.16B, <Vm>.4B[index]")]
        public void Udot_Ve([Values(3u)] uint rd,
                            [Values(4u)] uint rn,
                            [Values(5u)] uint rm)
        {
            // Index 1 selects bytes 4..7 (all 0xFF = 255): 4*255 = 1020 (0x3FC).
            uint opcode = WithRegs(0x6FA8E0E6u, rd, rn, rm);

            V128 v3 = MakeVectorE0E1(0ul, 0ul);
            V128 v4 = MakeVectorE0E1(0x0101010101010101ul, 0x0101010101010101ul);
            V128 v5 = MakeVectorE0E1(0xFFFFFFFF00000000ul, 0ul);

            ExecutionContext context = SingleOpcode(opcode, v3: v3, v4: v4, v5: v5, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(3)), Is.EqualTo(0x000003FC000003FCul));
                Assert.That(GetVectorE1(context.GetV(3)), Is.EqualTo(0x000003FC000003FCul));
            });
        }

        [Test, Description("SDOT <Vd>.4S with high registers accumulates")]
        public void Sdot_V_HighRegs([Values(31u)] uint rd,
                                    [Values(30u)] uint rn,
                                    [Values(5u)] uint rm)
        {
            // [10, 20, 30, 40] plus 4*(1*3) = 12 per lane gives [22, 32, 42, 52].
            uint opcode = WithRegs(0x4E81941Fu, rd, rn, rm);

            V128 v31 = MakeVectorE0E1(0x000000140000000Aul, 0x000000280000001Eul);
            V128 v30 = MakeVectorE0E1(0x0101010101010101ul, 0x0101010101010101ul);
            V128 v5 = MakeVectorE0E1(0x0303030303030303ul, 0x0303030303030303ul);

            ExecutionContext context = SingleOpcode(opcode, v30: v30, v31: v31, v5: v5, runUnicorn: false);

            Assert.Multiple(() =>
            {
                Assert.That(GetVectorE0(context.GetV(31)), Is.EqualTo(0x0000002000000016ul));
                Assert.That(GetVectorE1(context.GetV(31)), Is.EqualTo(0x000000340000002Aul));
            });
        }

        [Test, Description("Assembler ground-truth words decode to dot-product instructions")]
        public void GroundTruthWords_DecodeToDotProd(
            [Values(0x4E829420u, 0x0E829420u, 0x4E81941Fu)] uint sdotWord,
            [Values(0x6E829420u, 0x2E829420u, 0x2E9D97DFu)] uint udotWord,
            [Values(0x4F82E020u, 0x4FA2E020u, 0x4F82E820u, 0x4FA2E820u,
                    0x0F82E020u, 0x0FA2E820u, 0x4F93E820u)] uint sdotElemWord,
            [Values(0x6FA8E0E6u)] uint udotElemWord)
        {
            // Encodings captured from aarch64-linux-gnu-as 2.46.1
            // (-march=armv8.2-a+dotprod); see .nx2-logs/asm/dotprod.s.
            // A table collision would decode these to the wrong instruction.
            Assert.Multiple(() =>
            {
                Assert.That(OpCodeTable.GetInstA64(unchecked((int)sdotWord)).inst.Name, Is.EqualTo(InstName.Sdot_V));
                Assert.That(OpCodeTable.GetInstA64(unchecked((int)udotWord)).inst.Name, Is.EqualTo(InstName.Udot_V));
                Assert.That(OpCodeTable.GetInstA64(unchecked((int)sdotElemWord)).inst.Name, Is.EqualTo(InstName.Sdot_Ve));
                Assert.That(OpCodeTable.GetInstA64(unchecked((int)udotElemWord)).inst.Name, Is.EqualTo(InstName.Udot_Ve));
            });
        }

        [Test, Description("USDOT (I8MM) words do not decode as SDOT/UDOT")]
        public void UsdotWord_DoesNotDecodeAsDotProd(
            [Values(0x4E829C20u, 0x0E879CC5u)] uint usdotWord)
        {
            // USDOT shares the SDOT top bits but differs at bits [15:10]
            // (0b100111 vs 0b100101); it is a separate I8MM feature and must
            // not be claimed by the DotProd entries.
            InstName name = OpCodeTable.GetInstA64(unchecked((int)usdotWord)).inst.Name;

            Assert.Multiple(() =>
            {
                Assert.That(name, Is.Not.EqualTo(InstName.Sdot_V));
                Assert.That(name, Is.Not.EqualTo(InstName.Udot_V));
                Assert.That(name, Is.Not.EqualTo(InstName.Sdot_Ve));
                Assert.That(name, Is.Not.EqualTo(InstName.Udot_Ve));
            });
        }
    }
}
