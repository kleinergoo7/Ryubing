using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // nx2:gpu session. Commands 101/102/211-215 keep the GPU accessible memory registry in
    // Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory; the GPU memory manager resolves those ranges,
    // mirrors queue semaphore pages and serves sparse (GPU only) regions.
    class INx2GpuSession : IpcService
    {
        private const uint NumIds = 32;
        private const ulong VaStart = 0x8000000;
        private const ulong VaEnd = 0xC000000000;

        private static readonly object _eventsLock = new();
        private static readonly Dictionary<uint, KEvent> _events = [];
        private static readonly List<KEvent> _allEvents = [];

        // Our GPU completes work synchronously, so id events are also signalled periodically;
        // a wait issued after the last job call would otherwise block forever.
        public static void SignalAllNx2GpuEvents()
        {
            lock (_eventsLock)
            {
                for (int i = 0; i < _allEvents.Count; i++)
                {
                    _allEvents[i].ReadableEvent.Signal();
                }
            }
        }

        public INx2GpuSession() : base(registerTipc: true) { }

        private KEvent EventFor(ServiceCtx context, uint id)
        {
            id %= NumIds;

            lock (_eventsLock)
            {
                if (!_events.TryGetValue(id, out KEvent evt))
                {
                    evt = new KEvent(context.Device.System.KernelContext);
                    _events[id] = evt;
                    _allEvents.Add(evt);
                }

                return evt;
            }
        }

        [CommandTipc(11)]
        public ResultCode GetIdRange(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(NumIds - 1);

            return ResultCode.Success;
        }

        [CommandTipc(12)]
        public ResultCode GetIdEvent(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();

            Trace($"GetIdEvent id={id}");
            KEvent evt = EventFor(context, id);

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(21)]
        public ResultCode Start(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();

            EventFor(context, id).ReadableEvent.Signal();

            return ResultCode.Success;
        }

        [CommandTipc(22)]
        public ResultCode OpenSubSession(ServiceCtx context)
        {
            MakeSessionObject(context, new INx2GpuSession());

            return ResultCode.Success;
        }

        [CommandTipc(33)]
        public ResultCode GetCount(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(201)]
        public ResultCode GetUserPartition(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)VaStart);
            context.ResponseData.Write((uint)(VaStart >> 32));
            context.ResponseData.Write(unchecked((uint)(VaEnd - VaStart)));
            context.ResponseData.Write(unchecked((uint)((VaEnd - VaStart) >> 32)));

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        public ResultCode GetGsvPartition(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        [CommandTipc(203)]
        public ResultCode GetVaRange(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write((uint)VaStart);
            context.ResponseData.Write((uint)(VaStart >> 32));
            context.ResponseData.Write(unchecked((uint)(VaEnd - VaStart)));
            context.ResponseData.Write(unchecked((uint)((VaEnd - VaStart) >> 32)));

            return ResultCode.Success;
        }

        // Sub 101: (start u64, last u64) after CreateGpuAccessibleMemory. The reply's word 3 is
        // the memory handle that map jobs (211) name.
        [CommandTipc(101)]
        public ResultCode RegisterMemory(ServiceCtx context)
        {
            Trace("RegisterMemory");
            ulong start = context.RequestData.ReadUInt64();
            ulong last = context.RequestData.ReadUInt64();

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);

            uint handle = 0;

            if (last >= start)
            {
                handle = Nx2GpuMemory.Register(start, last - start + 1);
            }
            else
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"nx2:gpu 101 invalid range [0x{start:X}, 0x{last:X}]");
            }

            context.ResponseData.Write(handle);

            return ResultCode.Success;
        }

        // Sub 102: word 2 the memory handle, word 3 the job slot. The game frees the memory and
        // reuses it as ordinary heap right after, so the registration and its mappings go away.
        [CommandTipc(102)]
        public ResultCode DestroyMemory(ServiceCtx context)
        {
            uint handle = context.RequestData.ReadUInt32();
            uint slot = context.RequestData.ReadUInt32();

            if (!Nx2GpuMemory.Destroy(handle))
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"nx2:gpu 102 unknown memory handle {handle}");
            }

            CompleteJobs(context, slot);

            return ResultCode.Success;
        }

        // Sub 211 (NormalMapGpuMemory4K): words 3-4 first GPU address, 5-6 last, 7 memory handle,
        // 8-9 offset into that memory, 17 job slot.
        [CommandTipc(211)]
        public ResultCode MapMemory(ServiceCtx context)
        {
            Trace("MapMemory");
            context.RequestData.ReadUInt32(); // Job id, constant.

            ulong first = context.RequestData.ReadUInt64();
            ulong last = context.RequestData.ReadUInt64();
            uint handle = context.RequestData.ReadUInt32();
            ulong offset = context.RequestData.ReadUInt64();

            context.RequestData.BaseStream.Seek(7 * sizeof(uint), System.IO.SeekOrigin.Current);
            uint slot = context.RequestData.ReadUInt32();

            if (last < first || !Nx2GpuMemory.Map(first, last - first + 1, handle, offset))
            {
                Logger.Warning?.Print(LogClass.KernelIpc,
                    $"nx2:gpu 211 map [0x{first:X}, 0x{last:X}] handle={handle} offset=0x{offset:X} failed");
            }

            CompleteJobs(context, slot);

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        // Sub 212 (NormalModifyMemoryKind4K): word 7 the wanted kind, word 10 the job slot. Kinds
        // do not matter to the emulated GPU, so keep the mapping and report the wanted kind.
        [CommandTipc(212)]
        public ResultCode ModifyMemoryKind(ServiceCtx context)
        {
            Trace("ModifyMemoryKind");
            context.RequestData.ReadUInt32(); // Job id, constant.

            context.RequestData.ReadUInt64(); // First GPU address.
            context.RequestData.ReadUInt64(); // Last GPU address.
            uint kind = context.RequestData.ReadUInt32() & 0xff;
            context.RequestData.ReadUInt32(); // Fallback kind.
            context.RequestData.ReadUInt32(); // Sync flag.
            uint slot = context.RequestData.ReadUInt32();

            CompleteJobs(context, slot);

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(kind);

            return ResultCode.Success;
        }

        // Sub 213 (NormalUnmapGpuMemory4K): words 3-4 first GPU address, 5-6 last, 8 job slot.
        [CommandTipc(213)]
        public ResultCode UnmapMemory(ServiceCtx context)
        {
            Trace("UnmapMemory");
            context.RequestData.ReadUInt32(); // Job id, constant.

            ulong first = context.RequestData.ReadUInt64();
            ulong last = context.RequestData.ReadUInt64();

            context.RequestData.ReadUInt32(); // Unknown.
            uint slot = context.RequestData.ReadUInt32();

            if (last >= first)
            {
                Nx2GpuMemory.Unmap(first, last - first + 1);
            }

            CompleteJobs(context, slot);

            return ResultCode.Success;
        }

        // Sub 214 (CreateSparseRegion): words 3-6 the GPU-only range, word 7 flags (bit 0x200 marks
        // the large mostly-empty regions), word 11 the job slot. The region is backed with host
        // memory eagerly; unbacked (over budget) still exists and reads zero.
        [CommandTipc(214)]
        public ResultCode CreateGpuOnlyRegion(ServiceCtx context)
        {
            Trace("CreateGpuOnlyRegion");
            context.RequestData.ReadUInt32(); // Job id, constant.

            ulong start = context.RequestData.ReadUInt64();
            ulong last = context.RequestData.ReadUInt64();
            context.RequestData.ReadUInt32(); // Flags.

            context.RequestData.BaseStream.Seek(3 * sizeof(uint), System.IO.SeekOrigin.Current);
            uint slot = context.RequestData.ReadUInt32();

            if (last >= start && Nx2GpuMemory.CreateSparse(start, last - start + 1))
            {
                Logger.Debug?.Print(LogClass.KernelIpc,
                    $"nx2:gpu 214 GPU-only region [0x{start:X}, 0x{last:X}] size=0x{last - start + 1:X}");
            }
            else
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"nx2:gpu 214 unexpected region [0x{start:X}, 0x{last:X}]");
            }

            CompleteJobs(context, slot);

            return ResultCode.Success;
        }

        // Sub 215 (DestroySparseRegion): words 3-6 the range, word 8 the job slot.
        [CommandTipc(215)]
        public ResultCode DestroyGpuOnlyRegion(ServiceCtx context)
        {
            Trace("DestroyGpuOnlyRegion");
            context.RequestData.ReadUInt32(); // Job id, constant.

            ulong first = context.RequestData.ReadUInt64();
            ulong last = context.RequestData.ReadUInt64();

            context.RequestData.ReadUInt32(); // Unknown.
            uint slot = context.RequestData.ReadUInt32();

            if (last >= first)
            {
                ulong size = last - first + 1;

                if (!Nx2GpuMemory.DestroySparse(first))
                {
                    // Not a sparse region: drop the mapping, like an unmap job.
                    Nx2GpuMemory.Unmap(first, size);
                }
            }

            CompleteJobs(context, slot);

            return ResultCode.Success;
        }

        // The job slot words are not fully understood yet, so completing a job wakes every id
        // event (as the first boot pass did) plus the named slot.
        private static int _traceCount;

        private static void Trace(string what)
        {
            if (System.Threading.Interlocked.Increment(ref _traceCount) > 20000)
            {
                return;
            }

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceNv, $"NX2G {what}");
        }

        private void CompleteJobs(ServiceCtx context, uint slot)
        {
            KEvent[] events;

            lock (_eventsLock)
            {
                events = new KEvent[_events.Count];
                _events.Values.CopyTo(events, 0);
            }

            for (int i = 0; i < events.Length; i++)
            {
                events[i].ReadableEvent.Signal();
            }

            EventFor(context, slot).ReadableEvent.Signal();
        }
    }
}
