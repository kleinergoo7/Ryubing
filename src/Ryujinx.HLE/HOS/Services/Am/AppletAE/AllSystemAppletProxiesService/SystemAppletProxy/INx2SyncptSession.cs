// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // nx2:syncpt TIPC surface. Slot events are backed by the GPU syncpoints: a request signals
    // its slot event once the counter (the bitwise NOT of the syncpoint id) reaches the value.
    class INx2SyncptSession : IpcService
    {
        private const uint HostSyncpoints = SynchronizationManager.MaxHardwareSyncpoints;

        private sealed class SlotRequest
        {
            public uint Syncpoint;
            public SyncpointWaiterHandle Handle;
            public bool Pending;
        }

        private readonly Dictionary<uint, KEvent> _events = [];
        private readonly Dictionary<uint, SlotRequest> _requests = [];
        private static int _dualLogs;
        private readonly object _lock = new();

        public INx2SyncptSession() : base(registerTipc: true) { }

        private KEvent GetEvent(ServiceCtx context, uint slot)
        {
            if (!_events.TryGetValue(slot, out KEvent evt))
            {
                evt = new KEvent(context.Device.System.KernelContext);
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.KernelIpc, $"NX2 syncpt event slot={slot} hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(evt.ReadableEvent)}");
                _events[slot] = evt;
            }

            return evt;
        }

        private static uint DecodeSyncpoint(uint counter)
        {
            return ~counter;
        }

        [CommandTipc(0)]
        public ResultCode Handshake(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            ulong token = context.RequestData.ReadUInt64();

            context.ResponseData.Write(token);

            return ResultCode.Success;
        }

        [CommandTipc(1)]
        public ResultCode GetCount(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(32u);

            return ResultCode.Success;
        }

        [CommandTipc(21)]
        public ResultCode GetSlot(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        [CommandTipc(22)]
        public ResultCode GetSlotEvent(ServiceCtx context)
        {
            uint slot = context.RequestData.ReadUInt32();
            KEvent evt = GetEvent(context, slot);

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            Logger.Debug?.Print(LogClass.KernelIpc, $"nx2:syncpt slot={slot} server event handle=0x{handle:X8}");

            // The guest SDK (nnSdk+0x5289f8) reads the event handle from TIPC response word 3
            // (TLS+0xC) and the result from word 4: header at 0/1, then the handle descriptor at 2.
            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(103)]
        public ResultCode QueryCounter(ServiceCtx context)
        {
            uint counter = context.RequestData.ReadUInt32();
            uint syncpoint = DecodeSyncpoint(counter);

            ulong value = 0;

            if (syncpoint < HostSyncpoints)
            {
                // nvhost increments the host set for submitted fences; the guest polls that
                // set through this call. (The GPU set moves only for command semaphores.)
                uint hostValue = context.Device.System.HostSyncpoint.ReadSyncpointValue(syncpoint);
                uint gpuValue = context.Device.Gpu.Synchronization.GetSyncpointValue(syncpoint);

                value = hostValue;

                if (_dualLogs < 40 && (hostValue != 0 || gpuValue != 0))
                {
                    _dualLogs++;
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:syncpt 103 sp={syncpoint} host={hostValue} gpu={gpuValue}");
                }
            }



            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)value);
            context.ResponseData.Write((uint)(value >> 32));
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        [CommandTipc(201)]
        public ResultCode RequestSlotEvent(ServiceCtx context)
        {
            uint slot = context.RequestData.ReadUInt32();
            uint syncpoint = DecodeSyncpoint(context.RequestData.ReadUInt32());
            ulong value = context.RequestData.ReadUInt64();
            uint mode = context.RequestData.ReadUInt32();

            Logger.Info?.Print(LogClass.ServiceNv, $"nx2:syncpt 201 slot={slot} syncpoint={syncpoint} value={value} mode={mode}");

            KEvent evt = GetEvent(context, slot);

            lock (_lock)
            {
                CancelLocked(context, slot);

                // A new request starts unsignalled: slots are reused and a stale signal would
                // make the guest believe the new request already completed.
                evt.ReadableEvent.Clear();

                if (syncpoint >= HostSyncpoints)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"nx2:syncpt 201 syncpoint {syncpoint} out of range, signalling");
                    evt.ReadableEvent.Signal();

                    return ResultCode.Success;
                }

                // nvhost already advanced its (host) fence past the requested value.
                if (context.Device.System.HostSyncpoint.ReadSyncpointValue(syncpoint) >= value)
                {
                    evt.ReadableEvent.Signal();

                    return ResultCode.Success;
                }

                SlotRequest request = new()
                {
                    Syncpoint = syncpoint,
                    Pending = true,
                };

                _requests[slot] = request;

                // The callback may run on the GPU thread before Register returns.
                request.Handle = context.Device.Gpu.Synchronization.RegisterCallbackOnSyncpoint(
                    syncpoint,
                    (uint)value,
                    _ =>
                    {
                        lock (_lock)
                        {
                            if (!request.Pending)
                            {
                                return;
                            }

                            request.Pending = false;
                        }

                        evt.ReadableEvent.Signal();
                    });

                if (request.Handle == null)
                {
                    // Already past the threshold; the callback was never registered.
                    request.Pending = false;
                    evt.ReadableEvent.Signal();
                }
            }

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        public ResultCode CancelSlotEvent(ServiceCtx context)
        {
            uint slot = context.RequestData.ReadUInt32();

            Logger.Debug?.Print(LogClass.ServiceNv, $"nx2:syncpt 202 slot={slot}");

            lock (_lock)
            {
                CancelLocked(context, slot);

                // The SDK waits on the slot event after a cancel, so signal it like a completion.
                GetEvent(context, slot).ReadableEvent.Signal();
            }

            return ResultCode.Success;
        }

        private void CancelLocked(ServiceCtx context, uint slot)
        {
            if (_requests.Remove(slot, out SlotRequest request) && request.Pending)
            {
                request.Pending = false;

                context.Device.Gpu.Synchronization.UnregisterCallback(request.Syncpoint, request.Handle);
            }
        }
    }
}
