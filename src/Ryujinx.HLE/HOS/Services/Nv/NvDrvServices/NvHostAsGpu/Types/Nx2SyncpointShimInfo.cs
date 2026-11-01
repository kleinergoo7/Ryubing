using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu.Types
{
    [StructLayout(LayoutKind.Sequential)]
    struct Nx2SyncpointShimInfo
    {
        public ulong Address;
        public uint Stride;
        public uint Count;
    }
}
