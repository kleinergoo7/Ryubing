using System;
using System.Buffers.Binary;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// A 128-bit native Ampere (SM86) instruction.
    /// </summary>
    public struct AmpereInstruction
    {
        public const int Size = 16;

        public uint W0;
        public uint W1;
        public uint W2;
        public uint W3;

        public AmpereInstruction(uint w0, uint w1, uint w2, uint w3)
        {
            W0 = w0;
            W1 = w1;
            W2 = w2;
            W3 = w3;
        }

        public uint Word(int index)
        {
            return index switch
            {
                0 => W0,
                1 => W1,
                2 => W2,
                3 => W3,
                _ => 0,
            };
        }

        /// <summary>
        /// Extracts a bit field that may cross the 32-bit word boundary.
        /// </summary>
        public uint Bits(int offset, int count)
        {
            int word = offset >> 5;
            ulong bits = Word(word);

            if (word + 1 < 4)
            {
                bits |= (ulong)Word(word + 1) << 32;
            }

            return (uint)((bits >> (offset & 31)) & ((1UL << count) - 1));
        }

        public static AmpereInstruction Read(ReadOnlySpan<byte> bytes)
        {
            return new AmpereInstruction(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        }
    }
}
