// SPDX-License-Identifier: MIT
// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Progress counters for the NX2 diagnostics (submits/presents per minute).
    static class Nx2EventNames
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Names = new();

        public static void Register(object evt, string name)
        {
            Names[System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(evt)] = name;
        }

        public static string Lookup(object obj)
        {
            return obj != null && Names.TryGetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj), out string name) ? name : null;
        }
    }

    static class Nx2Counters
    {
        public static int Submits;
        public static int Presents;
    }

    static class Nx2NativeObjects
    {
        public sealed class Memory
        {
            public ulong Size;
            public ulong Address;
        }

        public sealed class Image
        {
            public uint Memory;
            public uint[] Preference = new uint[16];
            public ulong Size;
            public uint Count = 1;
            public bool WindowPool;
        }

        public static readonly object Lock = new();
        public static readonly Dictionary<uint, Memory> Memories = [];
        public static readonly Dictionary<uint, Image> Images = [];

        public static uint NextMemory = 1;
        public static uint NextImage = 1;

        public static readonly System.Collections.Generic.HashSet<uint> AttachedAddressSpaces = [];

        public static (uint Width, uint Height, ulong Pitch, uint BlockHeightLog2, ulong Size) Layout(uint[] preference)
        {
            // The image preference is always 16 words; refuse a short array instead of
            // reading past its end.
            if (preference == null || preference.Length < 16)
            {
                throw new ArgumentOutOfRangeException(nameof(preference), "NX2 image preference needs 16 words");
            }

            uint width = (preference[2] & 0xFFFF) + 1;
            uint height = (preference[2] >> 16) + 1;
            ulong pitchGobs = preference[5];
            uint blockHeightLog2 = preference[4];
            ulong pitch;

            if (blockHeightLog2 <= 8 && pitchGobs >= (width * 4UL + 63) / 64 && pitchGobs <= 4096)
            {
                pitch = pitchGobs * 64;
            }
            else
            {
                pitch = AlignUp(width * 4UL, 64);
                blockHeightLog2 = 7;
            }

            ulong blockRows = 1UL << (int)blockHeightLog2;
            ulong size = pitch * AlignUp(height, blockRows);

            return (width, height, pitch, blockHeightLog2, size);
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        // Console GOB swizzle table (Eglantine block_linear_unswizzle_3d_bcn.comp).
        public static uint SwizzleTable(uint pos)
        {
            ReadOnlySpan<uint> table =
            [
                0x12100200, 0x13110301, 0x16140604, 0x17150705,
                0x1a180a08, 0x1b190b09, 0x1e1c0e0c, 0x1f1d0f0d,
            ];

            uint index = pos >> 4;
            uint value = (table[(int)(index / 4)] >> (int)((index % 4) * 8)) & 0xff;

            return (value << 4) | (pos & 0xf);
        }

        public static uint SwizzleOffset(uint byteX, uint y)
        {
            uint offset = SwizzleTable((y & 7) * 64 + (byteX & 63));

            // Switch 2 "native tiling": the console's GOB swizzle differs from Maxwell's.
            uint difference = ((offset >> 5) ^ (offset >> 6)) & 1;

            return offset ^ ((difference << 5) | (difference << 6));
        }

        public static void Unswizzle(byte[] source, byte[] destination, int width, int height, ulong pitch, int blockHeightLog2)
        {
            int gobsInX = (int)(pitch / 64);
            int blockHeight = Math.Max(blockHeightLog2 - 3, 0);
            int blockHeightMask = (1 << blockHeight) - 1;
            int blockSize = gobsInX << (9 + blockHeight);

            for (int y = 0; y < height; y++)
            {
                int blockY = y >> 3;
                int rowBase = ((blockY >> blockHeight) * blockSize) + ((blockY & blockHeightMask) << 9);
                int destinationRow = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    uint byteX = (uint)x * 4;
                    int offset = rowBase + (int)((byteX >> 6) << (9 + blockHeight)) + (int)SwizzleOffset(byteX, (uint)y);

                    int sourceIndex = offset;
                    int destinationIndex = destinationRow + x * 4;

                    destination[destinationIndex + 0] = source[sourceIndex + 0];
                    destination[destinationIndex + 1] = source[sourceIndex + 1];
                    destination[destinationIndex + 2] = source[sourceIndex + 2];
                    destination[destinationIndex + 3] = source[sourceIndex + 3];
                }
            }
        }
    }
}
