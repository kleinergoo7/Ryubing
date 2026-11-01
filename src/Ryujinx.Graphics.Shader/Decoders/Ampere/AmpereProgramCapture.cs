using System;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Detects the end of a native (SM86) shader program.
    /// </summary>
    public sealed class AmpereProgramCapture
    {
        public const uint MaximumInstructions = 65536;

        public enum Result
        {
            Continue,
            Complete,
            InvalidTarget,
        }

        private uint _lastCallTarget;
        private uint _lastForwardTarget;
        private bool _mainExited;
        private bool _computedBranch;

        public Result Observe(AmpereInstruction instruction, uint index)
        {
            uint opcode = instruction.Bits(0, 12);

            if (opcode == 0x944 || opcode == 0x947 || opcode == 0x945)
            {
                ulong raw = instruction.W1 | ((ulong)(instruction.W2 & 0x3ffff) << 32);
                long relative = opcode == 0x945
                    ? unchecked((int)instruction.W1)
                    : unchecked((long)(raw << 14)) >> 14;
                long target = (long)index * 16 + 16 + relative;

                if (target < 0 || target % 16 != 0 || target >= MaximumInstructions * 16)
                {
                    return Result.InvalidTarget;
                }

                // CALL.REL.NOINC (bit 86) pushes no return address: the compiler uses it as a jump
                // (the lowering treats it as one), so it never pairs with a RET.
                bool noincCall = opcode == 0x944 && (instruction.W2 & 0x400000) != 0;

                if (opcode == 0x944 && !noincCall)
                {
                    _lastCallTarget = Math.Max(_lastCallTarget, (uint)(target / 16));
                }
                else
                {
                    _lastForwardTarget = Math.Max(_lastForwardTarget, (uint)(target / 16));
                }

                // The compiler ends every program with an unconditional branch to itself; with a jump
                // table (whose targets live in a constant buffer) that is the only reliable end.
                if (opcode == 0x947 && _computedBranch && instruction.Bits(12, 4) == 7 &&
                    target == (long)index * 16)
                {
                    return Result.Complete;
                }
            }

            if (opcode == 0x949 || opcode == 0x958)
            {
                _computedBranch = true;
            }

            if (_computedBranch)
            {
                return Result.Continue;
            }

            if (opcode == 0x94d && instruction.Bits(12, 4) == 7 && index >= _lastForwardTarget)
            {
                if (_lastCallTarget == 0)
                {
                    return Result.Complete;
                }

                _mainExited = true;
            }

            if (opcode == 0x950 && instruction.Bits(12, 4) == 7 && _mainExited &&
                index >= _lastCallTarget && index >= _lastForwardTarget)
            {
                return Result.Complete;
            }

            return Result.Continue;
        }

        public uint LastCallTarget => _lastCallTarget;
    }
}
