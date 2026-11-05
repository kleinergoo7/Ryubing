// SPDX-License-Identifier: MIT
// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
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
                    Nx2EventNames.Register(evt.ReadableEvent, $"gpuid{id}");
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
            // An empty partition is min = ~0, max = 0 (min > max). (0, 0) reads as a one-byte
            // partition at address 0, which aborts nn::gpu::sdk setup.
            context.ResponseData.Write(uint.MaxValue);
            context.ResponseData.Write(uint.MaxValue);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        // 41(name u64, flags u64, debug label u64): returns a graphics device id that later calls
        // name; 0 is not a valid id.
        [CommandTipc(41)]
        public ResultCode CreateGraphicsDevice(ServiceCtx context)
        {
            uint id = (uint)System.Threading.Interlocked.Increment(ref _nextDeviceId);

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(id);

            return ResultCode.Success;
        }

        [CommandTipc(42)]
        public ResultCode DestroyGraphicsDevice(ServiceCtx context)
        {
            context.RequestData.ReadUInt32();

            return ResultCode.Success;
        }

        private static int _nextDeviceId;

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

            SignalSlotAfterGpu(context, slot);

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
            else if (System.Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") == "1" &&
                System.Threading.Interlocked.Increment(ref _mapLogs) <= 40)
            {
                // Capped probe (AGENTS.md section 9): map arguments distinguish genuine
                // streaming progress (advancing ranges) from a retry loop (same range
                // re-mapped forever because its reads return zeros).
                Logger.Warning?.Print(LogClass.KernelIpc,
                    $"nx2:gpu 211 map [0x{first:X}, 0x{last:X}] handle={handle} offset=0x{offset:X} slot={slot}");
            }

            SignalSlotNow(context, slot);

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        // Sub 212 (NormalModifyMemoryKind4K): word 7 the wanted kind, word 10 the job slot.
        // WORKAROUND: kinds do not matter to the emulated GPU, so the mapping is kept and the
        // wanted kind is echoed back. Logged per call at Debug; revisit if a title checks the
        // applied kind against hardware capabilities.
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

            SignalSlotNow(context, slot);

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

            SignalSlotAfterGpu(context, slot);

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
            uint flags = context.RequestData.ReadUInt32();

            context.RequestData.BaseStream.Seek(3 * sizeof(uint), System.IO.SeekOrigin.Current);
            uint slot = context.RequestData.ReadUInt32();

            // Flag 0x200 marks mostly-empty terrain/pool regions, backed lazily as the GPU
            // writes them; the rest the console backs with real memory up front. Regions of
            // hundreds of MiB up to several GiB are sparse on the console too (a title asks for
            // two 6 GiB pools): committing those up front runs the budget out, the later regions
            // get no backing at all and every GPU write into them is lost.
            const ulong EagerRegionLimit = 256UL << 20;
            ulong size = last >= start ? last - start + 1 : 0;
            bool eager = (flags & 0x200) == 0 && size <= EagerRegionLimit;

            if (last >= start && Nx2GpuMemory.CreateRegion(start, last - start + 1, eager))
            {
                Logger.Debug?.Print(LogClass.KernelIpc,
                    $"nx2:gpu 214 GPU-only region [0x{start:X}, 0x{last:X}] size=0x{last - start + 1:X} eager={eager}");
            }
            else
            {
                Logger.Warning?.Print(LogClass.KernelIpc,
                    $"nx2:gpu 214 rejected region [0x{start:X}, 0x{last:X}]; reporting failure instead of a fake success");

                return ResultCode.MemoryAllocationFailed;
            }

            SignalSlotNow(context, slot);

            return ResultCode.Success;
        }

        // Sub 216 (ExecuteSparseMapRequests, behind nvnMemoryPoolMapVirtual and NvRm's remap): an A
        // buffer of 40-byte requests and a B buffer that gets the kind applied to each. This is how
        // a game commits and releases its sparse texture tiles; without it the tiles never map.
        // Word 14 is the job slot, the reply is the number of requests done.
        [CommandTipc(216)]
        public ResultCode ExecuteSparseMapRequests(ServiceCtx context)
        {
            // The request is 12-15 words depending on the SDK version: the job slot follows the
            // constant/address-space/request-size words. Read it when present.
            uint slot = 0;
            long requestWords = context.RequestData.BaseStream.Length / sizeof(uint);

            if (requestWords > 12)
            {
                context.RequestData.BaseStream.Seek(12 * sizeof(uint), System.IO.SeekOrigin.Begin);
                slot = context.RequestData.ReadUInt32();
            }

            int count = 0;

            try
            {
                if (context.Request.SendBuff.Count > 0)
                {
                    ulong ptr = context.Request.SendBuff[0].Position;
                    ulong length = context.Request.SendBuff[0].Size;

                    count = (int)(length / 40);

                    for (int i = 0; i < count; i++)
                    {
                        ulong baseAddr = ptr + (ulong)i * 40;

                        ulong gpuAddress = context.Memory.Read<ulong>(baseAddr);
                        ulong size = context.Memory.Read<ulong>(baseAddr + 8);
                        uint handle = context.Memory.Read<uint>(baseAddr + 24);
                        ulong offset = context.Memory.Read<ulong>(baseAddr + 32);

                        if (size == 0)
                        {
                            continue;
                        }

                        // A zero handle unmaps the range; anything else maps it at the request's
                        // GPU address. Kinds do not matter to the emulated GPU.
                        if (handle == 0)
                        {
                            Nx2GpuMemory.Unmap(gpuAddress, size);
                        }
                        else
                        {
                            Nx2GpuMemory.Map(gpuAddress, size, handle, offset);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"nx2:gpu 216 sparse map failed: {ex.GetType().Name}: {ex.Message}");
            }

            SignalSlotAfterGpu(context, slot);

            // Count as u64 (two words, Eglantine SubSparseMap); a single word leaves the
            // high half stale and the guest reads a garbage request count.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)count);
            context.ResponseData.Write((uint)((ulong)count >> 32));

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

            SignalSlotAfterGpu(context, slot);

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

        private static int _jobLogs;

        private static int _mapLogs;

        // Job completion (Eglantine SignalAfterGpu split): map/create/kind jobs
        // complete at once, since their mappings are usable immediately; unmap and
        // destroy jobs complete once the GPU thread has drained everything queued so
        // far, so in-flight reads keep their backing.
        private void SignalSlotNow(ServiceCtx context, uint slot)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") == "1" &&
                System.Threading.Interlocked.Increment(ref _jobLogs) <= 40)
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"NX2 job complete slot={slot} cmd={context.Request.Type}");
            }

            EventFor(context, slot).ReadableEvent.Signal();
        }

        private void SignalSlotAfterGpu(ServiceCtx context, uint slot)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") == "1" &&
                System.Threading.Interlocked.Increment(ref _jobLogs) <= 40)
            {
                Logger.Warning?.Print(LogClass.KernelIpc, $"NX2 job complete slot={slot} cmd={context.Request.Type}");
            }

            KEvent evt = EventFor(context, slot);

            context.Device.Gpu.GPFifo.RunAfterGpu(() => evt.ReadableEvent.Signal());
        }
    }
}
