using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Decoders.Ampere
{
    /// <summary>
    /// Maps native program counters to lowered Maxwell code offsets.
    /// </summary>
    /// <remarks>
    /// Appended to the code as unreachable data: the offsets, then the entry count,
    /// then <see cref="Magic"/>. Offsets are relative to the start of the code region,
    /// excluding the 0x50-byte graphics header (Ryujing's decoder address space).
    /// </remarks>
    public static class NativeAddressMap
    {
        public const ulong Magic = 0x50414D4E4545444EUL;

        public const uint MaximumInstructions = 65536;

        public static bool Has(ReadOnlySpan<ulong> code)
        {
            return code.Length >= 2 && code[^1] == Magic;
        }

        public static void Append(List<ulong> code, ReadOnlySpan<uint> offsets)
        {
            if (offsets.IsEmpty)
            {
                return;
            }

            foreach (uint index in offsets)
            {
                code.Add(AmpereLayout.AddressOf(index));
            }

            code.Add((ulong)offsets.Length);
            code.Add(Magic);
        }

        /// <summary>
        /// Returns the lowered Maxwell offset for a native byte offset, if the code carries a map.
        /// </summary>
        public static uint? FindAddress(ReadOnlySpan<ulong> code, uint address)
        {
            if (!Has(code) || (address & 15) != 0)
            {
                return null;
            }

            ulong count = code[^2];

            if (count == 0 || count > MaximumInstructions || count + 2 > (ulong)code.Length)
            {
                return null;
            }

            if (address / 16 >= count)
            {
                return null;
            }

            int start = code.Length - (int)count - 2;
            ulong target = code[start + (int)(address / 16)];

            if (target < 8 || target >= (ulong)start * 8 || (target & 7) != 0 || (target & 31) == 0)
            {
                return null;
            }

            return (uint)target;
        }
    }
}
