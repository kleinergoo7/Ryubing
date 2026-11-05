// SPDX-License-Identifier: MIT
// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Minimal nx2:vi2 session. Presentation is acknowledged but not displayed yet.
    class INx2Vi2Session : IpcService
    {
        // Native vsync slots (Eglantine INx2Vi2Session: 8 indexed slots shared by
        // 31/32 acquire and 261-272 bind/unbind; a slot fires on vblank while bound).
        // Unbound slots fire too (the game waits on a fresh slot before binding it),
        // while bind-change slots fire once and go quiet.
        private const int NativeSlots = 8;
        private readonly KEvent[] _nativeSlots = new KEvent[NativeSlots];
        private readonly HashSet<KEvent> _vblankEvents = [];
        private System.Threading.Timer _tickTimer;

        public INx2Vi2Session() : base(registerTipc: true) { }

        private void EnsureTickTimer()
        {
            lock (_vblankEvents)
            {
                // Oracle drives the frame loop off a 60 Hz vblank; the tick also
                // resumes stalled GPU channels and signalled id events.
                _tickTimer ??= new System.Threading.Timer(_ =>
                {
                    lock (_vblankEvents)
                    {
                        foreach (KEvent tick in _vblankEvents)
                        {
                            tick.WritableEvent.Signal();
                        }
                    }

                    Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.TryResumeAll();
                    Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.NvHostGpuDeviceFile.SignalAllNx2Events();
                    INx2GpuSession.SignalAllNx2GpuEvents();

                    LogProgress();
                }, null, 16, 16);
            }
        }

        private void SetVsyncSlot(KEvent evt, bool on)
        {
            lock (_vblankEvents)
            {
                if (on)
                {
                    _vblankEvents.Add(evt);
                }
                else
                {
                    _vblankEvents.Remove(evt);
                }
            }
        }

        private KEvent NativeSlot(ServiceCtx context, uint index)
        {
            if (index >= NativeSlots)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 native slot {index} out of range, handing out nothing");
                return null;
            }

            KEvent evt = _nativeSlots[index];

            if (evt == null)
            {
                evt = new KEvent(context.Device.System.KernelContext);
                _nativeSlots[index] = evt;
                Nx2EventNames.Register(evt.ReadableEvent, $"vi2native{index}");
                SetVsyncSlot(evt, true);
                EnsureTickTimer();
            }

            return evt;
        }

        private static int _progressTicks;

        private static Ryujinx.HLE.Switch _device;

        // Host syncpoint values 0..7 for the render-gate question: the game waits on
        // channel fence values, and a fence that never advances parks its render loop.
        private static string SpValues()
        {
            Ryujinx.HLE.Switch device = System.Threading.Volatile.Read(ref _device);

            if (device?.System?.HostSyncpoint == null)
            {
                return "?";
            }

            System.Text.StringBuilder sb = new();

            for (uint id = 0; id < 8; id++)
            {
                if (id != 0)
                {
                    sb.Append(',');
                }

                sb.Append(device.System.HostSyncpoint.ReadSyncpointValue(id));
            }

            return sb.ToString();
        }

        private static void LogProgress()
        {
            if (System.Threading.Interlocked.Increment(ref _progressTicks) % 3600 != 0)
            {
                return;
            }

            string calls = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                        Ryujinx.HLE.HOS.Services.IpcService.CallCounts, kv => kv.Value), 24),
                kv => $"{kv.Key}={kv.Value}"));

            string ioctls = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Services.Nv.INvDrvServices.IoctlCounts, kv => kv.Value), 10),
                kv => $"{kv.Key}={kv.Value}"));

            string gpu = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(Ryujinx.HLE.HOS.Services.IpcService.CallCounts, kv => kv.Key.StartsWith("INx2GpuSession")),
                kv => $"{kv.Key.Substring("INx2GpuSession".Length)}= {kv.Value}".Replace("= ", "=")));

            string sites = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.GuestCallSites, kv => kv.Value), 8),
                kv => $"pid{kv.Key.Pid}:0x{kv.Key.Pc:X}={kv.Value}" +
                    (Ryujinx.HLE.HOS.Kernel.SupervisorCall.Syscall.GuestCallSiteInfo.TryGetValue(kv.Key, out string info) ? $"[{info}]" : "")));

            string missing = string.Join(' ', System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(
                    Ryujinx.HLE.HOS.Services.IpcService.MissingCalls, kv => kv.Value), 12),
                kv => $"{kv.Key}={kv.Value}"));

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2 progress t={_progressTicks / 60}s submits={Nx2Counters.Submits} presents={Nx2Counters.Presents} sparseCommit={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseCommitted >> 20}MiB dropped={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseDropped >> 20}MiB draws={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Draws} unbound={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.UnboundDrops} mme={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods}/{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacroMemoHits}+{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacroMemoMisses} 3d={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()} emitted=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.DescribeEmitted()}] sp=[{SpValues()}] hot=[{HotPcs()}]");
            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2 calls {calls} ioctls=[{ioctls}] gpu=[{gpu}] sites=[{sites}] missing=[{missing}]");
        }

        [CommandTipc(11)]
        public ResultCode ReturnEvent(ServiceCtx context)
        {
            // Fresh event per call (Eglantine ReturnEvent): the oracle never signals
            // it, so sharing one would satisfy a new wait with an old signal.
            KEvent evt = new(context.Device.System.KernelContext);
            Nx2EventNames.Register(evt.ReadableEvent, "vi2event11");

            // The oracle never signals this event; signalling it spuriously kept the
            // guest's main loop spinning in its handler instead of advancing.
            if (System.Threading.Interlocked.Increment(ref _event11Logs) <= 2)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    "NX2 vi2 command 11 event handed out unsignalled");
            }

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(1)]
        [CommandTipc(3)]
        public ResultCode ReturnOne(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(5)]
        public ResultCode GetTimestampRecorderCount(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(7u);

            return ResultCode.Success;
        }

        [CommandTipc(31)]
        [CommandTipc(32)]
        public ResultCode GetVsyncEvent(ServiceCtx context)
        {
            // Indexed native slots (Eglantine 31 acquire / 32 duplicate): the slot
            // index is the first data word (copy handles travel separately).
            uint index = 0;

            try
            {
                index = context.RequestData.ReadUInt32();
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 slot acquire without index: {ex.GetType().Name}");
            }

            KEvent evt = NativeSlot(context, index);

            if (evt == null)
            {
                return ResultCode.Success;
            }

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(51)]
        public ResultCode CreateLayer(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);

            return ResultCode.Success;
        }

        [CommandTipc(101)]
        [CommandTipc(201)]
        [CommandTipc(251)]
        [CommandTipc(111)]
        [CommandTipc(261)]
        public ResultCode SucceedQuietly(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        // 103(name): u32 refresh rate in Hz then a bool; the SDK accepts only 60 or 120.
        [CommandTipc(103)]
        public ResultCode QueryDisplayRefreshRate(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(60u);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        // 104(name): u64 output format, u16 peak nits. The SDK takes 1 as SDR and 2 as HDR and
        // fails the query on anything else; an empty reply left it reading the request's leftovers.
        [CommandTipc(104)]
        public ResultCode QueryDisplayHdrStatus(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(1u);   // SDR
            context.ResponseData.Write(0u);
            context.ResponseData.Write(400u); // paper white, nits

            return ResultCode.Success;
        }

        // 105(name): u16 paper white in nits (BT.2408 reference white).
        [CommandTipc(105)]
        public ResultCode QueryDisplayHdrPaperWhite(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(203u);

            return ResultCode.Success;
        }

        // 106(name): u64 highest buffer resolution mode of the display. 1 -> 720p, 2 -> 1080p,
        // 4 and 8 -> 1440p; 0 (an empty reply) falls back to 720p. Docked reports 4, handheld 2.
        [CommandTipc(106)]
        public ResultCode GetBufferMaxResolution(ServiceCtx context)
        {
            ulong mode = context.Device.System.State.DockedMode ? 4UL : 2UL;

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)mode);
            context.ResponseData.Write((uint)(mode >> 32));

            return ResultCode.Success;
        }

        // 212(layer): u64 bits of the layer's buffer slots that are ready. The client polls it
        // once a second until some are set; an empty reply left it on zero forever.
        [CommandTipc(212)]
        public ResultCode GetLayerBufferBits(ServiceCtx context)
        {
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0x7FFFu);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        private static int _modeResolutionLogs;

        // 263/265/267/269/271(name[8B, "Default"], u32 slot index): bind that vsync
        // slot to a display-change source and fire it once so the game reads the current
        // state (Eglantine BindChange; none of them change here). The reply is empty
        // success, not data. (261 BindDisplayVsyncEvent stays on SucceedQuietly:
        // slots tick unconditionally here, so binding is a no-op.)
        [CommandTipc(263)]
        [CommandTipc(265)]
        [CommandTipc(267)]
        [CommandTipc(269)]
        [CommandTipc(271)]
        public ResultCode BindDisplayChangeEvent(ServiceCtx context)
        {
            byte[] name = context.RequestData.ReadBytes(8);
            uint index = context.RequestData.ReadUInt32();

            if (index < NativeSlots && _nativeSlots[index] != null)
            {
                KEvent evt = _nativeSlots[index];

                SetVsyncSlot(evt, false);
                evt.ReadableEvent.Signal();
            }

            if (System.Threading.Interlocked.Increment(ref _modeResolutionLogs) <= 8)
            {
                string display = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0');
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 bind change cmd={(int)context.Request.Type - 0x10} name={display} slot={index}");
            }

            return ResultCode.Success;
        }

        // 262/264/266/268/270/272: detach the slot from its source (Eglantine
        // UnbindNativeEvent). The reply is empty success.
        [CommandTipc(262)]
        [CommandTipc(264)]
        [CommandTipc(266)]
        [CommandTipc(268)]
        [CommandTipc(270)]
        [CommandTipc(272)]
        public ResultCode UnbindDisplayChangeEvent(ServiceCtx context)
        {
            _ = context.RequestData.ReadBytes(8);
            uint index = context.RequestData.ReadUInt32();

            if (index < NativeSlots && _nativeSlots[index] != null)
            {
                SetVsyncSlot(_nativeSlots[index], false);
            }

            return ResultCode.Success;
        }

        private static int _unknown351Logs;

        // 351(u64, u64): purpose unknown (captured: (0xD01D728, 5) via NX2_MISSING_LOG).
        // WORKAROUND: named success with a capped per-trigger word log instead of a
        // silent auto-stub, so the reply shape can be inferred from guest behavior.
        [CommandTipc(351)]
        public ResultCode Unknown351(ServiceCtx context)
        {
            ulong first = context.RequestData.ReadUInt64();
            ulong second = context.RequestData.ReadUInt64();

            if (System.Threading.Interlocked.Increment(ref _unknown351Logs) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 vi2 351 first=0x{first:X} second=0x{second:X}");
            }

            return ResultCode.Success;
        }

        [CommandTipc(228)]
        [CommandTipc(229)]
        public ResultCode Present(ServiceCtx context)
        {
            // 229(u64 client token, u32 slot, u32 image, ...fences): word 5 after the
            // TIPC header is the presented image id; RequestData starts at word 2.
            context.RequestData.ReadUInt64();  // token
            context.RequestData.ReadUInt32();  // slot

            uint imageId = context.RequestData.ReadUInt32();

            System.Threading.Volatile.Write(ref _device, context.Device);
            System.Threading.Interlocked.Increment(ref Nx2Counters.Presents);

            if (System.Environment.GetEnvironmentVariable("NX2_METHOD_LOG") == "1" &&
                System.Threading.Interlocked.Increment(ref _methodSamples) % 300 == 0)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"NX2 3dmethods {Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Describe3DMethods()} macros={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.MacrosRun} emitted={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.EmittedMethods} draws={Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Draws} tol=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Tolerances}] fifo=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.Fifo}] compute=[{Ryujinx.Graphics.Gpu.Engine.GPFifo.Nx2GpuStall.ComputeMethods()}]");
            }

            if (System.Environment.GetEnvironmentVariable("NX2_PC_SAMPLE") == "1" &&
                System.Threading.Interlocked.Increment(ref _pcSamples) % 120 == 0)
            {
                SampleGuestPcs(context.Device);
                SampleGuestWaits(context.Device);
            }

            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1")
            {
                // The executing thread here is the HLE server thread (ServerBase.ServerLoop),
                // not the guest caller: attribute presents by client only where the message
                // carries a caller pid (CMIF), never by thread owner.
                var caller = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();

                if (caller != null)
                {
                    string key = $"{caller.Owner.Pid}:{imageId}";

                    if (PresentSeen.TryAdd(key, 0) && PresentSeen.Count <= 12)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present caller={caller.Owner.Pid} tid={caller.ThreadUid} image={imageId}");
                    }
                }
            }

            // Guest stacks of every application thread, once, at a choosable present count:
            // NX2_THREAD_DUMP=<present number>. Prints the call path each parked thread is in.
            if (System.Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") is string dumpAt &&
                int.TryParse(dumpAt, out int dumpPresent) &&
                Nx2Counters.Presents == dumpPresent)
            {
                foreach (System.Collections.Generic.KeyValuePair<ulong, Ryujinx.HLE.HOS.Kernel.Process.KProcess> proc in context.Device.System.KernelContext.Processes)
                {
                    if (proc.Key != 109)
                    {
                        continue;
                    }

                    foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in proc.Value.DebugThreads)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 td uid={thread.ThreadUid} name={thread.GetThreadName()} pc=0x{thread.Context?.Pc:X}");

                        try
                        {
                            thread.PrintGuestStackTrace();
                        }
                        catch (System.Exception ex)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 td failed {ex.GetType().Name}");
                        }
                    }
                }
            }

            if (System.Threading.Interlocked.Increment(ref _presentLogs) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present #{Nx2Counters.Presents} image={imageId}");
            }

            if (System.Threading.Interlocked.Increment(ref _lenLogs) <= 6)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present request length={context.RequestData.BaseStream.Length} pos={context.RequestData.BaseStream.Position}");
            }

            // The presented image's GPU fence travels in words 22/24 (the id is stored as its
            // bitwise NOT). Pace the guest on it, bounded, like the oracle does.
            try
            {
                System.IO.Stream stream = context.RequestData.BaseStream;

                // 24-word request: fence id at word 20, value at word 22 (after the 4 words read above).
                if (stream.Length - stream.Position >= 19 * sizeof(uint))
                {
                    // Words 6/7 after the header: unknown flags + the drawn-area size
                    // ((height - 1) << 16 | (width - 1), Eglantine PresentClientBuffer w[7]).
                    // Read-only probe for the image-size question; the stream is left where
                    // the fence parse below expects it.
                    uint word6 = context.RequestData.ReadUInt32();
                    uint shownSize = context.RequestData.ReadUInt32();

                    if (System.Threading.Interlocked.Increment(ref _shownLogs) <= 12)
                    {
                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present words w6=0x{word6:X} shown=0x{shownSize:X} (image #{Nx2Counters.Presents})");
                    }

                    stream.Seek(14 * sizeof(uint), System.IO.SeekOrigin.Current);

                    uint fenceRawId = context.RequestData.ReadUInt32();
                    context.RequestData.ReadUInt32();
                    uint fenceValue = context.RequestData.ReadUInt32();
                    uint fenceId = ~fenceRawId;

                    // Wait for the previous frame's fence, like the console: the guest prepares
                    // frame N+1 while the GPU still runs frame N.
                    uint waitId = _previousFenceId;
                    uint waitValue = _previousFenceValue;

                    _previousFenceId = fenceId;
                    _previousFenceValue = fenceValue;

                    if (System.Threading.Interlocked.Increment(ref _fenceLogs) <= 12)
                    {
                        uint now = waitId < MaxSyncpoints ? context.Device.Gpu.Synchronization.GetSyncpointValue(waitId) : 0;

                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present fence id={fenceId} value={fenceValue} (previous {waitId},{waitValue}) now={now}");
                    }

                    if (waitId < MaxSyncpoints && waitValue != 0)
                    {
                        // Up to 4 s, like the console path: the CPU runs frames ahead while the
                        // GPU is still compiling shaders, and a 1 s cap made presents fail.
                        bool reached = context.Device.Gpu.Synchronization.WaitOnSyncpoint(
                            waitId, waitValue, TimeSpan.FromSeconds(4));

                        if (!reached && System.Threading.Interlocked.Increment(ref _fenceTimeouts) <= 8)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                                $"NX2 present fence {waitId},{waitValue} not reached in 4 s");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A short request just skips the pacing; anything else is a real bug.
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 present fence parse failed: {ex.GetType().Name}: {ex.Message}");
            }

            Composite(context, imageId);

            // The display consumed the frame: advance the display syncpoint the guest waits on
            // after Present. INx2SyncptSession.GetSlot hands out slot 0, and the guest polls
            // QueryCounter(0) in a loop; nvhost channel fences use their own ids (1..n), so
            // without this advance that poll never changes and the render loop never restarts.
            if (DisplaySyncpoint < MaxSyncpoints)
            {
                uint now = context.Device.Gpu.Synchronization.IncrementSyncpoint(DisplaySyncpoint);

                if (System.Environment.GetEnvironmentVariable("NX2_ARB_LOG") == "1" &&
                    System.Threading.Interlocked.Increment(ref _displaySyncLogs) <= 8)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 display syncpoint {DisplaySyncpoint} -> {now} (present #{Nx2Counters.Presents})");
                }
            }

            // Oracle: reply is a u64 mask of buffer slots the client may acquire next
            // (all free = AllSlotsFreeMask) plus four zero words. A zero mask stalls the producer.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(AllSlotsFreeMask);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            // Frame-paced vsync: the game starts frames on the native slot events.
            lock (_vblankEvents)
            {
                foreach (KEvent evt in _vblankEvents)
                {
                    evt.WritableEvent.Signal();
                }
            }

            return ResultCode.Success;
        }

        // The CPU present runs on the guest's own present thread; it must stay cheap.
        // Disabled until the renderer submits real frames; static (not const) so the
        // disabled path does not trip unreachable-code warnings.
        private static readonly bool CompositeEnabled = true;

        private static void Composite(ServiceCtx context, uint imageId)
        {
            if (!CompositeEnabled)
            {
                return;
            }

            Nx2NativeObjects.Image image = null;
            ulong imageOffset = 0;
            ulong address = 0;

            lock (Nx2NativeObjects.Lock)
            {
                uint baseId = 0;

                foreach ((uint id, Nx2NativeObjects.Image candidate) in Nx2NativeObjects.Images)
                {
                    if (imageId >= id && imageId - id < candidate.Count && (image == null || id > baseId))
                    {
                        baseId = id;
                        image = candidate;
                        imageOffset = (imageId - id) * candidate.Size;
                    }
                }

                if (image == null || !Nx2NativeObjects.Memories.TryGetValue(image.Memory, out Nx2NativeObjects.Memory memory))
                {
                    return;
                }

                address = memory.Address + imageOffset;
            }

            (uint width, uint height, ulong pitch, uint blockHeightLog2, ulong size) = Nx2NativeObjects.Layout(image.Preference);

            // Guest-controlled dimensions: validate before any narrowing cast or allocation.
            // RGBA8 needs width * 4 * height bytes; reject anything that overflows an int.
            long pixelBytes;

            try
            {
                pixelBytes = checked((long)width * 4 * height);
            }
            catch (OverflowException)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId} dimensions overflow: {width}x{height}");

                return;
            }

            if (size > int.MaxValue || pixelBytes > int.MaxValue || width == 0 || height == 0)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId} size out of range: {width}x{height} size=0x{size:X}");

                return;
            }

            ulong pid = context.Device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !context.Device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId}: no active application process");

                return;
            }

            // Throttle: the CPU unswizzle copies up to ~16 MB per frame.
            if (System.Threading.Interlocked.Increment(ref _presentCount) % 8 != 0)
            {
                return;
            }

            byte[] source = RentBuffer(ref _sourceBuffer, (int)size);
            byte[] linear = RentBuffer(ref _linearBuffer, (int)pixelBytes);

            try
            {
                process.CpuMemory.Read(address, source.AsSpan(0, (int)size));
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 composite image={imageId} read of 0x{size:X} bytes at 0x{address:X} failed: {ex.GetType().Name}: {ex.Message}");

                return;
            }

            Nx2NativeObjects.Unswizzle(source, linear, (int)width, (int)height, pitch, (int)blockHeightLog2);

            // Diagnostic (AGENTS.md section 9): dump the first frames as raw RGBA so a
            // black screen can be attributed to the guest (black content) or to the
            // present path (content present but not displayed).
            if (Ryujinx.Common.Nx2ProbeLog.Enabled("NX2_FRAME_DUMP"))
            {
                int dumpIndex = System.Threading.Interlocked.Increment(ref _frameDumps);

                if (dumpIndex <= 3)
                {
                    string path = Ryujinx.Common.Nx2LogConfinement.ResolveLogPath($"frame-{dumpIndex}-{(int)width}x{(int)height}.rgba");

                    System.IO.File.WriteAllBytes(path, linear.AsSpan(0, (int)pixelBytes).ToArray());

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                        $"NX2 frame dump #{dumpIndex} -> {path} ({(int)width}x{(int)height})");
                }
            }

            // MKW draws into the top-left corner of its present images: 2560x1440 docked in
            // 3840x2160 images, 1920x1080 handheld.
            int cropWidth = (int)Math.Min(width, MkwCropWidth);
            int cropHeight = (int)Math.Min(height, MkwCropHeight);

            ImageCrop crop = new(0, cropWidth, 0, cropHeight, false, false, false, 1.0f, 1.0f);

            TextureCreateInfo info = new(
                (int)width,
                (int)height,
                1,
                1,
                1,
                1,
                1,
                4,
                Format.R8G8B8A8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            try
            {
                lock (PresentLock)
                {
                    // Reuse a small ring of host textures: the GPU may still be reading
                    // an earlier frame when the next one is submitted. Creating one per
                    // present leaked GPU memory until presents failed.
                    ITexture texture = RentPresentTexture(context, info, (int)width, (int)height);

                    texture.SetData(MemoryOwner<byte>.RentCopy(linear));

                    context.Device.Gpu.Renderer.Window.Present(texture, crop, static () => { });

                }
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Increment(ref _compositeLogs) <= 4)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 present failed: {ex.Message}");
                }

                return;
            }

            if (System.Threading.Interlocked.Increment(ref _compositeLogs) <= 4)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 composite image={imageId} addr=0x{address:X} {width}x{height} blockLog2={blockHeightLog2} pid={pid}");
            }

            // Let the render loop swap the UI over to the renderer host on the first frame.
            context.Device.Gpu.Window.SignalFrameReady();
            context.Device.Gpu.GPFifo.Interrupt();
        }

        private static int _frameDumps;

        private static readonly object PresentLock = new();
        private static readonly ITexture[] _presentTextures = new ITexture[3];
        private static int _presentTextureIndex;
        private static int _presentTextureWidth;
        private static int _presentTextureHeight;

        // A ring of reusable host textures, sized for the current frame. Must be
        // called with PresentLock held.
        private static ITexture RentPresentTexture(ServiceCtx context, TextureCreateInfo info, int width, int height)
        {
            if (_presentTextures[_presentTextureIndex] == null || width != _presentTextureWidth || height != _presentTextureHeight)
            {
                foreach (ITexture old in _presentTextures)
                {
                    (old as System.IDisposable)?.Dispose();
                }

                Array.Clear(_presentTextures, 0, _presentTextures.Length);
                _presentTextureIndex = 0;
                _presentTextureWidth = width;
                _presentTextureHeight = height;
            }

            ref ITexture slot = ref _presentTextures[_presentTextureIndex];
            _presentTextureIndex = (_presentTextureIndex + 1) % _presentTextures.Length;

            slot ??= context.Device.Gpu.Renderer.CreateTexture(info);

            return slot;
        }
        private static uint _previousFenceId = uint.MaxValue;
        private static int _event11Logs;
        private static uint _previousFenceValue;
        private static byte[] _sourceBuffer;
        private static byte[] _linearBuffer;
        private static int _presentCount;

        private static int _pcSamples;

        private static int _methodSamples;

        // Samples every guest thread's PC of the application process. The GDB stub reports one
        // shared trampoline for all paused threads, so this is the only usable per-thread view.
        private static void SampleGuestPcs(Ryujinx.HLE.Switch device)
        {
            ulong pid = device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                return;
            }

            System.Text.StringBuilder sb = new();

            foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in process.DebugThreads)
            {
                if (thread.Context == null)
                {
                    continue;
                }

                sb.Append($"{thread.ThreadUid}:{thread.GetThreadName()}=0x{thread.Context.Pc:X} ");

                // Track how many distinct PCs each thread visits: a parked thread scores 1,
                // a running loop scores many. That separates "sleeping" from "executing".
                string tid = $"{thread.ThreadUid}:{thread.GetThreadName()}";

                ThreadPcs.GetOrAdd(tid, _ => new System.Collections.Concurrent.ConcurrentDictionary<ulong, int>())
                    .AddOrUpdate(thread.Context.Pc, 1, (_, v) => v + 1);
            }

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi, $"NX2 pcs {sb}");
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<ulong, int>> ThreadPcs = new();

        /// <summary>Threads ranked by how many distinct PCs they visit, with their hottest PC.</summary>
        public static string HotPcs()
        {
            System.Text.StringBuilder sb = new();

            var ranked = new System.Collections.Generic.List<(string Tid, int Distinct, ulong Hot, int HotCount)>();

            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Concurrent.ConcurrentDictionary<ulong, int>> kv in ThreadPcs)
            {
                ulong hot = 0;
                int hotCount = 0;

                foreach (System.Collections.Generic.KeyValuePair<ulong, int> pc in kv.Value)
                {
                    if (pc.Value > hotCount)
                    {
                        hotCount = pc.Value;
                        hot = pc.Key;
                    }
                }

                ranked.Add((kv.Key, kv.Value.Count, hot, hotCount));
            }

            ranked.Sort((a, b) => b.Distinct.CompareTo(a.Distinct));

            for (int i = 0; i < ranked.Count && i < 14; i++)
            {
                sb.Append($"{ranked[i].Tid}[pc={ranked[i].Distinct},hot=0x{ranked[i].Hot:X}x{ranked[i].HotCount}] ");
            }

            return sb.ToString();
        }

        // Dumps what every application thread waits on (object type + server/waiter info).
        // Names the futex/event/session a parked loader or render thread is stuck on.
        private static void SampleGuestWaits(Ryujinx.HLE.Switch device)
        {
            ulong pid = device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                return;
            }

            foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in process.DebugThreads)
            {
                Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject[] waited = thread.WaitSyncObjects;

                if (waited == null || waited.Length == 0)
                {
                    continue;
                }

                System.Text.StringBuilder sb = new();

                foreach (Ryujinx.HLE.HOS.Kernel.Common.KSynchronizationObject obj in waited)
                {
                    string name = Nx2EventNames.Lookup(obj);
                    sb.Append(name != null ? $"{obj?.GetType().Name}({name}) " : $"{obj?.GetType().Name ?? "null"} ");
                }

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                    $"NX2 wait tid={thread.ThreadUid}({thread.GetThreadName()}) pc=0x{thread.Context?.Pc:X} objs=[{sb}]");
            }
        }

        private static int _waitSamples;

        private static int _fenceTimeouts;

        private static int _shownLogs;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> PresentSeen = new();

        private static int _compositeLogs;
        private static int _fenceLogs;
        private static int _presentLogs;
        private static int _lenLogs;

        // MKW draws into the top-left corner of its present images; crop to the drawn area.
        private const uint MkwCropWidth = 2560u;
        private const uint MkwCropHeight = 1440u;

        // All buffer slots reported free in the 229 reply.
        private const uint AllSlotsFreeMask = 0x7fffu;

        // Hardware syncpoint count; fence ids at or above it are not real syncpoints.
        private const uint MaxSyncpoints = 192u;

        // Syncpoint the display advances once per presented frame (the slot GetSlot hands out).
        private const uint DisplaySyncpoint = 0u;

        private static int _displaySyncLogs;

        private static byte[] RentBuffer(ref byte[] buffer, int size)
        {
            if (buffer == null || buffer.Length < size)
            {
                buffer = new byte[size];
            }

            return buffer;
        }
    }
}
