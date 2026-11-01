using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Progress counters for the NX2 diagnostics (submits/presents per minute).
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

        public static (uint Width, uint Height, ulong Pitch, uint BlockHeightLog2, ulong Size) Layout(uint[] preference)
        {
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
    }
}
