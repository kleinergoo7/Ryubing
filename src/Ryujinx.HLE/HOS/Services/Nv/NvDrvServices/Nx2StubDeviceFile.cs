// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices
{
    // Placeholder for nvgpu nodes that only Switch 2 opens (e.g. /dev/nvhost-tsg-gpu).
    // Every ioctl succeeds, echoes its input fields back, and is logged so the driver's use of
    // the node shows up.
    // WORKAROUND: success with zeroed output is load-bearing for Switch 2 channel bring-up
    // (the guest proceeds through TSG creation without a real subcontext id). Logged on every
    // trigger via the STUBBED warning below; replace with real TSG subcontext handling.
    class Nx2StubDeviceFile : NvDeviceFile
    {
        public Nx2StubDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner) { }

        public override void Close() { }

        private NvInternalResult Stub(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            string words = string.Join(' ', System.Linq.Enumerable.Select(
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(arguments[..(arguments.Length & ~3)]).ToArray(),
                w => w.ToString("X8")));

            Logger.Warning?.Print(LogClass.ServiceNv, $"(STUBBED) {Path} ioctl=0x{command.RawValue:X8} type={command.Type:X2} nr=0x{command.Number:X2} size=0x{arguments.Length:X} in=[{words}]");

            // arguments already holds the input; unknown in/out fields keep their values.
            inlineOutBuffer.Clear();

            return NvInternalResult.Success;
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            return Stub(command, arguments, Span<byte>.Empty);
        }

        public override NvInternalResult Ioctl2(NvIoctl command, Span<byte> arguments, Span<byte> inlineInBuffer)
        {
            return Stub(command, arguments, Span<byte>.Empty);
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            return Stub(command, arguments, inlineOutBuffer);
        }
    }
}
