// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.MME;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.Gpu
{
    public class MacroTuringTests
    {
        private sealed class FakeState : IDeviceState
        {
            public readonly List<(int Offset, int Value)> Writes = [];

            public int Read(int offset) => 0;

            public void Write(int offset, int data)
            {
                Writes.Add((offset, data));
            }
        }

        // One MME64 instruction: three words, low-to-high as words[2..0].
        // Field layout mirrors MacroTuring.Decode (bit 0 = End, then PredMode,
        // PredReg, per-lane Op/Dst/SrcA/SrcB, Imm words, then OutMethod/OutEmit).
        private static int[] Insn(
            bool end,
            uint op0 = 0, uint dst0 = 24, uint srcA0 = 24, uint srcB0 = 24, uint imm0 = 0,
            uint op1 = 0, uint dst1 = 24, uint srcA1 = 24, uint srcB1 = 24, uint imm1 = 0,
            uint outM0 = 0, uint outE0 = 0, uint outM1 = 0, uint outE1 = 0)
        {
            uint low = (end ? 1u : 0u) | (dst0 << 15) | (srcA0 << 20) | (srcB0 << 25) | ((imm0 & 3u) << 30);
            low |= (op0 & 31u) << 10;
            uint mid = ((imm0 >> 2) & 0x3FFFu) | ((op1 & 31u) << 14) | ((dst1 & 31u) << 19) | ((srcA1 & 31u) << 24) | ((srcB1 & 7u) << 29);
            uint high = ((srcB1 >> 3) & 3u) | ((imm1 & 0xFFFFu) << 2) | ((outM0 & 7u) << 18) | ((outE0 & 15u) << 21) | ((outM1 & 7u) << 25) | ((outE1 & 15u) << 28);

            return [(int)high, (int)mid, (int)low];
        }

        private static MacroTuring Turing(int[] dataRam = null)
        {
            return new MacroTuring(null, dataRam ?? new int[0xC00], () => 0);
        }

        [Test, Description("ADD of an immediate emits the result as a GPU method")]
        public void Execute_AddImmediateEmitsMethod()
        {
            // r1 = Imm0 + r0, then emit alu0 to the method named by Imm0.
            int[] code = Insn(end: true, op0: 0, dst0: 1, srcA0: 25, srcB0: 24, imm0: 0x1234, outM0: 5, outE0: 1);
            FakeState state = new();

            Turing().Execute(code, state, 0);

            Assert.That(state.Writes, Has.Count.EqualTo(1));
            Assert.That(state.Writes[0].Offset, Is.EqualTo(0x234 * 4));
            Assert.That(state.Writes[0].Value, Is.EqualTo(0x1234));
        }

        [Test, Description("Data RAM write followed by a read emits the stored word")]
        public void Execute_DataRamWriteThenRead()
        {
            int[] write = Insn(end: false, op0: 31, dst0: 24, srcA0: 25, srcB0: 26, imm0: 0x20, imm1: 0x1234);
            int[] read = Insn(end: true, op0: 30, dst0: 1, srcA0: 25, srcB0: 24, imm0: 0x20, outM0: 5, outE0: 1);
            int[] code = [.. write, .. read];
            FakeState state = new();

            Turing().Execute(code, state, 0);

            Assert.That(state.Writes, Has.Count.EqualTo(1));
            Assert.That(state.Writes[0].Offset, Is.EqualTo(0x20 * 4));
            Assert.That(state.Writes[0].Value, Is.EqualTo(0x1234));
        }

        [Test, Description("Reading past the parameter FIFO yields zero and counts a tolerated underflow")]
        public void Execute_FifoUnderflowIsTolerated()
        {
            int[] code = Insn(end: true, op0: 0, dst0: 24, srcA0: 28, srcB0: 29);
            FakeState state = new();
            int before = MacroTuring.TolFifoUnderflow;

            Turing().Execute(code, state, 0);

            Assert.That(MacroTuring.TolFifoUnderflow - before, Is.EqualTo(1));
            Assert.That(state.Writes, Is.Empty);
        }

        [Test, Description("Unsupported extended ALU op fails the macro without emitting")]
        public void Execute_InvalidAluOpEmitsNothing()
        {
            int[] code = Insn(end: true, op0: 7, dst0: 24, srcA0: 25, srcB0: 24, imm0: 1);
            FakeState state = new();

            Turing().Execute(code, state, 0);

            Assert.That(state.Writes, Is.Empty);
        }
    }
}
