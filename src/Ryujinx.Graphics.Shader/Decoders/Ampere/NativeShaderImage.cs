using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Builds a Maxwell shader image from a native Ampere program.
    /// </summary>
    public static class NativeShaderImage
    {
        /// <summary>
        /// Copies the guest shader header (if any), appends the lowered words with Ryujing's
        /// scheduling pads, then appends the native address map as unreachable data.
        /// </summary>
        public static byte[] Build(ReadOnlySpan<byte> guestShader, AmpereLoweredProgram lowered, bool hasHeader = true)
        {
            ArgumentNullException.ThrowIfNull(lowered);

            int headerSize = hasHeader ? AmpereLayout.HeaderSize : 0;
            List<ulong> code = [];

            foreach (ulong instruction in lowered.Instructions)
            {
                if ((code.Count * 8) % 32 == 0)
                {
                    code.Add(0); // Maxwell scheduling word, absent from native instructions.
                }

                code.Add(instruction);
            }

            NativeAddressMap.Append(code, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lowered.SourceInstructionOffsets));

            byte[] image = new byte[headerSize + code.Count * 8];

            int headerBytes = Math.Min(headerSize, guestShader.Length);

            if (headerBytes > 0)
            {
                guestShader[..headerBytes].CopyTo(image);
            }

            for (int index = 0; index < code.Count; index++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(headerSize + index * 8), code[index]);
            }

            return image;
        }

        /// <summary>
        /// Reads a raw native instruction stream.
        /// </summary>
        public static AmpereInstruction[] ReadInstructions(ReadOnlySpan<byte> data)
        {
            AmpereInstruction[] instructions = new AmpereInstruction[data.Length / AmpereInstruction.Size];

            for (int index = 0; index < instructions.Length; index++)
            {
                instructions[index] = AmpereInstruction.Read(data[(index * AmpereInstruction.Size)..]);
            }

            return instructions;
        }

        public static ulong[] ReadCodeWords(ReadOnlySpan<byte> image, bool hasHeader = true)
        {
            int offset = hasHeader ? AmpereLayout.HeaderSize : 0;

            if (offset > image.Length)
            {
                return [];
            }

            ReadOnlySpan<byte> code = image[offset..];

            ulong[] words = new ulong[code.Length / 8];

            for (int index = 0; index < words.Length; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt64LittleEndian(code[(index * 8)..]);
            }

            return words;
        }
    }
}
