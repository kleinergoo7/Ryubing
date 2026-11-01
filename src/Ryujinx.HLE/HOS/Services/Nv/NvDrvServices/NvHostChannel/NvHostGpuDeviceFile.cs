using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.Types;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel
{
    internal class NvHostGpuDeviceFile : NvHostChannelDeviceFile
    {
#pragma warning disable IDE0052 // Remove unread private member
        private readonly KEvent _smExceptionBptIntReportEvent;
        private readonly KEvent _smExceptionBptPauseReportEvent;
        private readonly KEvent _errorNotifierEvent;
#pragma warning restore IDE0052

        private int _smExceptionBptIntReportEventHandle;
        private int _smExceptionBptPauseReportEventHandle;
        private int _errorNotifierEventHandle;

        private readonly Dictionary<uint, (KEvent Event, int Handle)> _nx2Events = [];

        public NvHostGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, memory, owner)
        {
            _smExceptionBptIntReportEvent = CreateEvent(context, out _smExceptionBptIntReportEventHandle);
            _smExceptionBptPauseReportEvent = CreateEvent(context, out _smExceptionBptPauseReportEventHandle);
            _errorNotifierEvent = CreateEvent(context, out _errorNotifierEventHandle);
        }

        private KEvent CreateEvent(ServiceCtx context, out int handle)
        {
            KEvent evnt = new(context.Device.System.KernelContext);

            if (context.Process.HandleTable.GenerateHandle(evnt.ReadableEvent, out handle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            return evnt;
        }

        private static NvInternalResult Nx2Fallback(NvInternalResult result)
        {
            if (result == NvInternalResult.NotImplemented && NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                return NvInternalResult.Success;
            }

            return result;
        }

        private int _nx2Ioctl17Count;

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            if (NvHostCtrlGpuDeviceFile.Nx2Driver && command.Type == NvIoctl.NvHostMagic && command.Number == 0x17 && arguments.Length >= 16)
            {
                uint syncpoint = ChannelSyncpoint.Id;
                uint value = HostDevice.Gpu.Synchronization.GetSyncpointValue(syncpoint);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(arguments[0..], syncpoint);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(arguments[4..], value);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(arguments[8..], 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(arguments[12..], 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(arguments[14..], 1);

                return NvInternalResult.Success;
            }

            if (NvHostCtrlGpuDeviceFile.Nx2Driver && command.Type == NvIoctl.NvHostMagic && command.Number == 0x17)
            {
                if (_nx2Ioctl17Count++ < 3)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nvhost-gpu 0x17 in=0x{arguments.Length:X} data={System.Convert.ToHexString(arguments)}");
                }
            }

            NvInternalResult result = Nx2Fallback(base.Ioctl(command, arguments));

            if (NvHostCtrlGpuDeviceFile.Nx2Driver && command.Type == NvIoctl.NvHostMagic && command.Number == 0x17 && _nx2Ioctl17Count <= 3)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"nvhost-gpu 0x17 -> {result} out={System.Convert.ToHexString(arguments)}");
            }

            return result;
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            NvInternalResult result = base.Ioctl3(command, arguments, inlineOutBuffer);

            if (result == NvInternalResult.NotImplemented && NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                inlineOutBuffer.Clear();

                return NvInternalResult.Success;
            }

            return result;
        }

        public override NvInternalResult Ioctl2(NvIoctl command, Span<byte> arguments, Span<byte> inlineInBuffer)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvHostMagic)
            {
                switch (command.Number)
                {
                    case 0x1b:
                        Logger.Info?.Print(LogClass.ServiceNv, $"Ioctl2 submit args=0x{arguments.Length:X} inline=0x{inlineInBuffer.Length:X}");
                        result = CallIoctlMethod<SubmitGpfifoArguments, ulong>(SubmitGpfifoEx, arguments, inlineInBuffer);
                        break;
                }
            }

            return Nx2Fallback(result);
        }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            // TODO: accurately represent and implement those events.
            switch (eventId)
            {
                case 0x1:
                    eventHandle = _smExceptionBptIntReportEventHandle;
                    break;
                case 0x2:
                    eventHandle = _smExceptionBptPauseReportEventHandle;
                    break;
                case 0x3:
                    eventHandle = _errorNotifierEventHandle;
                    break;
                default:
                    // Switch 2 NVN multi-waits on the channel's events, event 7 included.
                    // Nothing signals them yet, but an invalid handle aborts the guest.
                    if (!NvHostCtrlGpuDeviceFile.Nx2Driver)
                    {
                        eventHandle = 0;
                        break;
                    }

                    if (!_nx2Events.TryGetValue(eventId, out (KEvent Event, int Handle) entry))
                    {
                        KEvent evnt = CreateEvent(Context, out int handle);
                        entry = (evnt, handle);
                        _nx2Events[eventId] = entry;

                        lock (_nx2EventRegistry)
                        {
                            _nx2EventRegistry.Add(evnt);
                        }

                        Logger.Warning?.Print(LogClass.ServiceNv, $"(STUBBED) Switch 2 channel event {eventId} handle=0x{handle:X} hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(evnt.ReadableEvent)}");
                    }

                    eventHandle = entry.Handle;
                    break;
            }

            return eventHandle != 0 ? NvInternalResult.Success : NvInternalResult.InvalidInput;
        }

        // Events the guest waits on for job completion. Our GPU completes work synchronously,
        // so they are also signalled periodically from the vi2 tick: a wait issued after the
        // last submission would otherwise block forever.
        private static readonly System.Collections.Generic.List<KEvent> _nx2EventRegistry = [];

        public static void SignalAllNx2Events()
        {
            lock (_nx2EventRegistry)
            {
                foreach (KEvent evt in _nx2EventRegistry)
                {
                    evt.ReadableEvent.Signal();
                }
            }
        }

        private NvInternalResult SubmitGpfifoEx(ref SubmitGpfifoArguments arguments, Span<ulong> inlineData)
        {
            return SubmitGpfifo(ref arguments, inlineData);
        }

        protected override void OnSubmission()
        {
            if (!NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                return;
            }

            // NVN's event pool waits on this channel's lazily created events for job
            // completion; the GPU work is queued, so report it as completed.
            foreach ((KEvent Event, int Handle) entry in _nx2Events.Values)
            {
                entry.Event.ReadableEvent.Signal();
            }
        }



        public override void Close()
        {
            if (_smExceptionBptIntReportEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_smExceptionBptIntReportEventHandle);
                _smExceptionBptIntReportEventHandle = 0;
            }

            if (_smExceptionBptPauseReportEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_smExceptionBptPauseReportEventHandle);
                _smExceptionBptPauseReportEventHandle = 0;
            }

            if (_errorNotifierEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_errorNotifierEventHandle);
                _errorNotifierEventHandle = 0;
            }

            foreach ((KEvent _, int handle) in _nx2Events.Values)
            {
                Context.Process.HandleTable.CloseHandle(handle);
            }

            _nx2Events.Clear();

            base.Close();
        }
    }
}
