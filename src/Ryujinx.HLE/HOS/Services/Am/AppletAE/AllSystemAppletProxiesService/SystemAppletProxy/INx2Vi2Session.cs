using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Minimal nx2:vi2 session. Presentation is acknowledged but not displayed yet.
    class INx2Vi2Session : IpcService
    {
        private readonly Dictionary<uint, KEvent> _events = [];
        private readonly List<KEvent> _tickEvents = [];
        private System.Threading.Timer _tickTimer;

        public INx2Vi2Session() : base(registerTipc: true) { }

        private void TrackTickEvent(KEvent evt)
        {
            lock (_tickEvents)
            {
                _tickEvents.Add(evt);

                // Oracle drives the frame loop off a 60 Hz vblank. The game also parks
                // on the command-11 event, so tick every session event, not just 31/32.
                _tickTimer ??= new System.Threading.Timer(_ =>
                {
                    lock (_tickEvents)
                    {
                        foreach (KEvent tick in _tickEvents)
                        {
                            tick.WritableEvent.Signal();
                        }
                    }

                    Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.NvHostGpuDeviceFile.SignalAllNx2Events();
                    INx2GpuSession.SignalAllNx2GpuEvents();

                    LogProgress();
                }, null, 16, 16);
            }
        }

        private static int _progressTicks;

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
                kv => $"pid{kv.Key.Pid}:0x{kv.Key.Pc:X}={kv.Value}"));

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                $"NX2 progress t={_progressTicks / 60}s submits={Nx2Counters.Submits} presents={Nx2Counters.Presents} sparseCommit={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseCommitted >> 20}MiB dropped={Ryujinx.Graphics.Gpu.Memory.Nx2GpuMemory.SparseDropped >> 20}MiB sites=[{sites}] gpu=[{gpu}]");
        }

        private KEvent EventFor(ServiceCtx context, uint slot)
        {
            if (!_events.TryGetValue(slot, out KEvent evt))
            {
                evt = new KEvent(context.Device.System.KernelContext);
                _events[slot] = evt;
            }

            return evt;
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

        [CommandTipc(11)]
        public ResultCode ReturnEvent(ServiceCtx context)
        {
            KEvent evt = EventFor(context, 0);

            TrackTickEvent(evt);

            if (context.Process.HandleTable.GenerateHandle(evt.ReadableEvent, out int handle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandTipc(31)]
        [CommandTipc(32)]
        public ResultCode GetVsyncEvent(ServiceCtx context)
        {
            // Fresh event per call (oracle creates one per call); signalled on each Present.
            KEvent evt = new(context.Device.System.KernelContext);

            TrackTickEvent(evt);

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
        [CommandTipc(212)]
        [CommandTipc(251)]
        [CommandTipc(111)]
        [CommandTipc(261)]
        [CommandTipc(104)]
        [CommandTipc(105)]
        public ResultCode SucceedQuietly(ServiceCtx context)
        {
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

            System.Threading.Interlocked.Increment(ref Nx2Counters.Presents);

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
                    stream.Seek(16 * sizeof(uint), System.IO.SeekOrigin.Current);

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
                        uint now = waitId < 192 ? context.Device.Gpu.Synchronization.GetSyncpointValue(waitId) : 0;

                        Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceVi,
                            $"NX2 present fence id={fenceId} value={fenceValue} (previous {waitId},{waitValue}) now={now}");
                    }

                    if (waitId < 192 && waitValue != 0)
                    {
                        context.Device.Gpu.Synchronization.WaitOnSyncpoint(waitId, waitValue, TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch
            {
                // A short request just skips the pacing.
            }

            Composite(context, imageId);

            // Oracle: reply is a u64 mask of buffer slots the client may acquire next
            // (all free = 0x7fff) plus four zero words. A zero mask stalls the producer.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0x7fffu);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);

            // Frame-paced vsync: the game starts frames on the 31/32 event.
            lock (_tickEvents)
            {
                foreach (KEvent evt in _tickEvents)
                {
                    evt.WritableEvent.Signal();
                }
            }

            return ResultCode.Success;
        }

        // The CPU present runs on the guest's own present thread; it must stay cheap.
        private const bool CompositeEnabled = false;

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

            ulong pid = context.Device.Processes.ActiveApplication?.ProcessId ?? 0;

            if (pid == 0 || !context.Device.System.KernelContext.Processes.TryGetValue(pid, out Ryujinx.HLE.HOS.Kernel.Process.KProcess process))
            {
                return;
            }

            // Throttle: the CPU unswizzle copies up to ~16 MB per frame.
            if (System.Threading.Interlocked.Increment(ref _presentCount) % 8 != 0)
            {
                return;
            }

            byte[] source = RentBuffer(ref _sourceBuffer, (int)size);
            byte[] linear = RentBuffer(ref _linearBuffer, (int)(width * 4 * height));

            try
            {
                process.CpuMemory.Read(address, source.AsSpan(0, (int)size));
            }
            catch
            {
                return;
            }

            Unswizzle(source, linear, (int)width, (int)height, pitch, (int)blockHeightLog2);

            DumpFrame(linear, (int)width, (int)height);

            // MKW draws into the top-left corner of its present images: 2560x1440 docked in
            // 3840x2160 images, 1920x1080 handheld.
            int cropWidth = (int)Math.Min(width, 2560u);
            int cropHeight = (int)Math.Min(height, 1440u);

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
                    ITexture texture = context.Device.Gpu.Renderer.CreateTexture(info);

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

        private static readonly object PresentLock = new();
        private static uint _previousFenceId = uint.MaxValue;
        private static int _fenceLogs;
        private static int _presentLogs;
        private static int _lenLogs;
        private static uint _previousFenceValue;
        private static byte[] _sourceBuffer;
        private static byte[] _linearBuffer;
        private static int _presentCount;
        private static int _compositeLogs;

        private static byte[] RentBuffer(ref byte[] buffer, int size)
        {
            if (buffer == null || buffer.Length < size)
            {
                buffer = new byte[size];
            }

            return buffer;
        }

        // Debug: dump the unswizzled present image as raw RGBA so it can be checked
        // without an X capture. Remove with the rest of the NX2 diagnostics.
        private static void DumpFrame(byte[] linear, int width, int height)
        {
            int count = System.Threading.Interlocked.Increment(ref _dumpCount);

            if (count > 6 || count % 2 != 0)
            {
                return;
            }

            try
            {
                string path = $"/home/reng/projects/switch2-test/ryujinx-logs/frame-{count:D2}.rgba";
                System.IO.File.WriteAllBytes(path, linear.AsSpan(0, width * height * 4).ToArray());
            }
            catch
            {
                // ignored
            }
        }

        private static int _dumpCount;

        private static uint SwizzleTable(uint pos)
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

        private static uint SwizzleOffset(uint byteX, uint y)
        {
            uint offset = SwizzleTable((y & 7) * 64 + (byteX & 63));

            // Switch 2 "native tiling": the console's GOB swizzle differs from Maxwell's.
            uint difference = ((offset >> 5) ^ (offset >> 6)) & 1;

            return offset ^ ((difference << 5) | (difference << 6));
        }

        private static void Unswizzle(byte[] source, byte[] destination, int width, int height, ulong pitch, int blockHeightLog2)
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
