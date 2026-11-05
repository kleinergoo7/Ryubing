// SPDX-License-Identifier: MIT
// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    // Minimal nx2:gsv TIPC surface. Commands without a handler are auto-stubbed.
    class INx2GraphicsSystemSession : IpcService
    {
        public INx2GraphicsSystemSession() : base(registerTipc: true) { }

        [CommandTipc(4)]
        public ResultCode Boot(ServiceCtx context)
        {
            // Eglantine Stub: ResponseBuilder rb{ctx, 2}; rb.Push(ResultSuccess);
            // pushes result + one extra zero word. The game reads this extra word.
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);
            return ResultCode.Success;
        }

        [CommandTipc(11)]
        public ResultCode Unknown11(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        [CommandTipc(101)]
        public ResultCode Unknown101(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        private static uint[] ReadPreference(ServiceCtx context)
        {
            uint[] preference = new uint[16];

            for (int i = 0; i < preference.Length; i++)
            {
                preference[i] = context.RequestData.ReadUInt32();
            }

            return preference;
        }

        [CommandTipc(111)]
        public ResultCode CreateNativeMemory(ServiceCtx context)
        {
            _ = context.RequestData.ReadUInt32();
            ulong size = context.RequestData.ReadUInt64();

            lock (Nx2NativeObjects.Lock)
            {
                uint id = Nx2NativeObjects.NextMemory++;
                Nx2NativeObjects.Memories[id] = new Nx2NativeObjects.Memory { Size = size };

                context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
                context.ResponseData.Write(id);
            }

            return ResultCode.Success;
        }

        [CommandTipc(423)]
        public ResultCode BindNativeGpuMemory(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();
            ulong address = context.RequestData.ReadUInt64();
            ulong size = context.RequestData.ReadUInt64();

            BindNativeMemory(id, address, size);

            return ResultCode.Success;
        }

        // 114(u32 id, u64 address, u64 size, u64 flags, u64 offset): backs native memory
        // with process memory (Eglantine BindNativeMemory; flags/offset are informational here).
        [CommandTipc(114)]
        public ResultCode BindNativeProcessMemory(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();
            ulong address = context.RequestData.ReadUInt64();
            ulong size = context.RequestData.ReadUInt64();

            BindNativeMemory(id, address, size);

            return ResultCode.Success;
        }

        // 112(u32 id): frees a native memory object (Eglantine DestroyNativeMemory).
        [CommandTipc(112)]
        public ResultCode DestroyNativeMemory(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();

            lock (Nx2NativeObjects.Lock)
            {
                Nx2NativeObjects.Memories.Remove(id);
            }

            return ResultCode.Success;
        }

        // 430(u32 id): frees a native image object (Eglantine DestroyNativeImage).
        [CommandTipc(430)]
        public ResultCode DestroyNativeImage(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();

            lock (Nx2NativeObjects.Lock)
            {
                Nx2NativeObjects.Images.Remove(id);
            }

            return ResultCode.Success;
        }

        private static void BindNativeMemory(uint id, ulong address, ulong size)
        {
            lock (Nx2NativeObjects.Lock)
            {
                if (!Nx2NativeObjects.Memories.TryGetValue(id, out Nx2NativeObjects.Memory memory))
                {
                    memory = new Nx2NativeObjects.Memory();
                    Nx2NativeObjects.Memories[id] = memory;
                }

                memory.Address = address;

                if (memory.Size == 0)
                {
                    memory.Size = size;
                }
            }
        }

        [CommandTipc(435)]
        public ResultCode CalculateNativeImageSize(ServiceCtx context)
        {
            uint[] preference = ReadPreference(context);
            uint count = context.RequestData.ReadUInt32();
            (_, _, _, _, ulong size) = Nx2NativeObjects.Layout(preference);

            size *= Math.Max(count, 1);

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)size);
            context.ResponseData.Write((uint)(size >> 32));

            return ResultCode.Success;
        }

        // 434(u32 id from nx2:gpu 33): the session takes a reference on that GPU
        // address space (Eglantine AttachGpuAddressSpace). Empty reply.
        [CommandTipc(434)]
        public ResultCode AttachGpuAddressSpace(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();

            lock (Nx2NativeObjects.Lock)
            {
                Nx2NativeObjects.AttachedAddressSpaces.Add(id);
            }

            if (System.Threading.Interlocked.Increment(ref _unknown434Logs) <= 4)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"nx2:gsv 434 attach address space {id}");
            }

            return ResultCode.Success;
        }

        // Eglantine CalculateRequirements: the client passes a 0x40-byte
        // InterModuleUserImagePreference (16 words) plus a u32 and expects the block-linear image
        // size and alignment. A zero size made the window init fail, after which
        // wsi::NativeWindow::Acquire dereferenced a null window (NX2_NOTES #33).
        [CommandTipc(1510)]
        public ResultCode CalculateRequirements(ServiceCtx context)
        {
            uint[] preference = ReadPreference(context);
            _ = context.RequestData.ReadUInt32();
            (_, _, _, uint blockHeightLog2, ulong size) = Nx2NativeObjects.Layout(preference);
            ulong alignment = Math.Max(64UL << (int)blockHeightLog2, 0x1000UL);

            Logger.Warning?.Print(LogClass.ServiceAm,
                $"nx2:gsv 1510 kind={preference[1]} size=0x{size:X} align=0x{alignment:X}");

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)size);
            context.ResponseData.Write((uint)(size >> 32));
            context.ResponseData.Write((uint)alignment);
            context.ResponseData.Write((uint)(alignment >> 32));

            return ResultCode.Success;
        }

        [CommandTipc(429)]
        public ResultCode CreateNativeImage(ServiceCtx context)
        {
            uint[] preference = ReadPreference(context);
            uint count = Math.Max(context.RequestData.ReadUInt32(), 1);
            uint memory = context.RequestData.ReadUInt32();
            (_, _, _, _, ulong size) = Nx2NativeObjects.Layout(preference);

            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1")
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"nx2:gsv 429 count={count} memory={memory} pref=[{string.Join(',', preference)}] size=0x{size:X}");
            }

            lock (Nx2NativeObjects.Lock)
            {
                uint id = Nx2NativeObjects.NextImage;
                Nx2NativeObjects.NextImage += count;

                Nx2NativeObjects.Images[id] = new Nx2NativeObjects.Image
                {
                    Memory = memory,
                    Preference = preference,
                    Size = size,
                    Count = count,
                };

                context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
                context.ResponseData.Write(id);
            }

            return ResultCode.Success;
        }

        [CommandTipc(314)]
        [CommandTipc(315)]
        public ResultCode ConfigureWindowImagePool(ServiceCtx context)
        {
            uint id = context.RequestData.ReadUInt32();
            bool create = (int)context.Request.Type - 0x10 == 315;

            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1")
            {
                Logger.Warning?.Print(LogClass.ServiceAm, $"nx2:gsv {(create ? 315 : 314)} id={id}");
            }

            lock (Nx2NativeObjects.Lock)
            {
                if (Nx2NativeObjects.Images.TryGetValue(id, out Nx2NativeObjects.Image image))
                {
                    image.WindowPool = create;
                }
            }

            return ResultCode.Success;
        }

        // 428(u32 image id) QueryNativeImage and 426(u32 id, u32, u32, u8) MapNativeImage:
        // GPU address, offset in memory and a GpuOunceUserImageDescriptor.
        [CommandTipc(426)]
        [CommandTipc(428)]
        public ResultCode QueryNativeImage(ServiceCtx context)
        {
            uint handle = context.RequestData.ReadUInt32();
            uint[] preference = new uint[16];
            ulong imageOffset = 0;
            ulong memoryAddress = 0;

            lock (Nx2NativeObjects.Lock)
            {
                uint baseId = 0;
                bool found = false;

                foreach ((uint id, Nx2NativeObjects.Image image) in Nx2NativeObjects.Images)
                {
                    if (id <= handle && handle - id < image.Count && (!found || id > baseId))
                    {
                        baseId = id;
                        found = true;
                    }
                }

                if (found)
                {
                    Nx2NativeObjects.Image image = Nx2NativeObjects.Images[baseId];
                    preference = image.Preference;
                    imageOffset = (handle - baseId) * image.Size;

                    if (Nx2NativeObjects.Memories.TryGetValue(image.Memory, out Nx2NativeObjects.Memory memory))
                    {
                        memoryAddress = memory.Address;
                    }
                }
            }

            (_, _, ulong pitch, uint blockHeightLog2, ulong size) = Nx2NativeObjects.Layout(preference);
            bool map = (int)context.Request.Type - 0x10 == 426;
            ulong gpuAddress = map ? memoryAddress + imageOffset : 0;

            uint[] words = new uint[37];
            words[0] = (uint)gpuAddress;
            words[1] = (uint)(gpuAddress >> 32);
            words[2] = (uint)imageOffset;
            words[3] = (uint)(imageOffset >> 32);

            // Descriptor starts at word 4.
            Span<uint> descriptor = words.AsSpan(4);
            descriptor[0] = 1;
            descriptor[1] = preference[1];
            descriptor[2] = preference[2];
            descriptor[4] = blockHeightLog2;
            descriptor[5] = (uint)(pitch / 64);
            descriptor[8] = 1;
            descriptor[9] = 1;
            descriptor[11] = (uint)size;

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);

            foreach (uint word in words)
            {
                context.ResponseData.Write(word);
            }

            return ResultCode.Success;
        }

        // 115(u64 requested feature flags): the subset that is granted, which is all
        // of them here (Eglantine QueryGrantedFeatures). A zeroed reply denies every
        // feature — the renderer never initializes and no frame work is ever submitted.
        [CommandTipc(115)]
        public ResultCode QueryGrantedFeatures(ServiceCtx context)
        {
            ulong requested = context.RequestData.ReadUInt64();

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write((uint)requested);
            context.ResponseData.Write((uint)(requested >> 32));

            return ResultCode.Success;
        }

        [CommandTipc(201)]
        [CommandTipc(301)]
        public ResultCode Stub201(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        [CommandTipc(305)]
        public ResultCode OpenViSession(ServiceCtx context)
        {
            MakeSessionObject(context, new INx2Vi2Session());

            return ResultCode.Success;
        }

        [CommandTipc(401)]
        [CommandTipc(402)]
        [CommandTipc(1101)]
        [CommandTipc(1102)]
        [CommandTipc(601)]
        public ResultCode Stub(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        // 991: nn::codec::CalculateDecoderImagePoolBuffer. The preference holds an output layout
        // code at word 1 and (width - 1) | (height - 1) << 16 at word 2. Auto-stubbed, the game
        // read the request's leftover words as the frame size and aborted with nn::diag::Abort.
        // Reply: u64 size, u64 alignment.
        [CommandTipc(991)]
        public ResultCode CalculateDecoderImagePoolBuffer(ServiceCtx context)
        {
            uint code = context.RequestData.ReadUInt32();
            uint packed = context.RequestData.ReadUInt32();

            ulong width = (packed & 0xFFFF) + 1;
            ulong height = (packed >> 16) + 1;

            // 1/4 NV12, 2/5 BGRA, 3/6 RGBA; the HDR codes are written as RGBA.
            bool nv12 = code is 1 or 4;
            ulong bytesPerPixel = nv12 ? 1UL : 4UL;
            const ulong BlockRows = 128;

            ulong pitch = ((width * bytesPerPixel) + 63UL) & ~63UL;
            ulong luma = pitch * ((height + BlockRows - 1) / BlockRows * BlockRows);
            ulong size = luma;

            if (nv12)
            {
                size += pitch * ((((height + 1) / 2) + BlockRows - 1) / BlockRows * BlockRows);
            }

            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(size);
            context.ResponseData.Write(4096UL);

            return ResultCode.Success;
        }

        // 902/906/1002: nn::codec::video::detail::ipc::Initialize expects a decoder session
        // handle; without one InitializeVp9Decoder fails with 2001-0114 and the title aborts when
        // a movie starts.
        [CommandTipc(901)]
        [CommandTipc(905)]
        [CommandTipc(1001)]
        public ResultCode StubVideoInit(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        [CommandTipc(902)]
        [CommandTipc(906)]
        [CommandTipc(1002)]
        public ResultCode OpenVideoSession(ServiceCtx context)
        {
            MakeSessionObject(context, new DummyService("nx2:vdec"));

            return ResultCode.Success;
        }

        // 602: nn::camera::sdk::CreateCameraContext expects the camera context session.
        [CommandTipc(602)]
        public ResultCode OpenCameraSession(ServiceCtx context)
        {
            MakeSessionObject(context, new DummyService("nx2:camera"));

            return ResultCode.Success;
        }

        private static int _unknown434Logs;

        // 1202: nn::chat::gsv expects a session; 1201 is its plain stub.
        [CommandTipc(1201)]
        public ResultCode StubChatInit(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        [CommandTipc(1202)]
        public ResultCode OpenChatSession(ServiceCtx context)
        {
            MakeSessionObject(context, new DummyService("nx2:chatgsv"));

            return ResultCode.Success;
        }

        [CommandTipc(202)]
        public ResultCode OpenSyncptSession(ServiceCtx context)
        {
            MakeSessionObject(context, new INx2SyncptSession());

            return ResultCode.Success;
        }

        [CommandTipc(497)]
        public ResultCode OpenGpuSession(ServiceCtx context)
        {
            MakeSessionObject(context, new INx2GpuSession());

            return ResultCode.Success;
        }

        // Offsets inside the client's read-only syncpoint page. The guest requires version 1 at
        // +0 and count such that ((count + 0x40) >> 6) - 1 < 0x40.
        private const uint SyncptValuesOffset = 0x40;
        private const uint SyncptRwSummaryOffset = 0x0;
        private const uint SyncptRoSummaryOffset = 0x1040;
        private const uint SyncptRwBitsOffset = 0x100;
        private const uint SyncptRoBitsOffset = 0x1100;
        private const uint SyncptPageSize = 0x2000;
        private const uint HostSyncpoints = 192;

        private readonly object _syncptLock = new();
        private IVirtualMemoryManager _syncptMemory;
        private NvHostSyncpt _syncptHost;
        private ulong _syncptRw;
        private ulong _syncptRo;
        private uint _syncptCount;
        private KEvent _syncptNotify;
        private System.Threading.Timer _syncptTimer;
        private bool _syncptFaulted;

        // Mirrors the host syncpoint values into the shared RO page and acknowledges the armed
        // bits, exactly like Eglantine's 500 us poll. Without the acknowledge the client's wait
        // on the notify event never completes and the frame loop parks forever.
        private void PollSyncpts()
        {
            lock (_syncptLock)
            {
                if (_syncptMemory == null || _syncptFaulted)
                {
                    return;
                }

                try
                {
                    for (uint id = 0; id < Math.Min(_syncptCount, HostSyncpoints); id++)
                    {
                        _syncptMemory.Write(_syncptRo + SyncptValuesOffset + id * 8, (ulong)_syncptHost.ReadSyncpointValue(id));
                    }

                    bool acked = false;
                    uint words = (_syncptCount + 63) / 64;

                    for (uint w = 0; w < words; w++)
                    {
                        ulong rwBits = _syncptMemory.Read<ulong>(_syncptRw + SyncptRwBitsOffset + w * 8);
                        ulong roAddress = _syncptRo + SyncptRoBitsOffset + w * 8;

                        if (_syncptMemory.Read<ulong>(roAddress) != rwBits)
                        {
                            _syncptMemory.Write(roAddress, rwBits);
                            acked = true;
                        }
                    }

                    _syncptMemory.Write(_syncptRo + SyncptRoSummaryOffset,
                        _syncptMemory.Read<ulong>(_syncptRw + SyncptRwSummaryOffset));

                    if (acked)
                    {
                        _syncptNotify.ReadableEvent.Signal();
                    }
                }
                catch (Exception ex)
                {
                    // The client may close the transfer memories while tearing down.
                    _syncptFaulted = true;
                    Logger.Warning?.Print(LogClass.ServiceAm, $"nx2:gsv 204 syncpt poll stopped: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Eglantine AttachSyncptMemory: the client passes an RW (0x1000) and an RO (0x2000)
        // transfer memory and expects a header in the RO page plus two events. It signals the
        // first event after arming a wait in the RW page and waits on the second until the
        // server copies the armed bits and summary into the RO page.
        [CommandTipc(204)]
        public ResultCode AttachSyncptMemory(ServiceCtx context)
        {
            uint versionRequested = context.RequestData.ReadUInt32();
            uint maxSyncpoints = Math.Min(context.RequestData.ReadUInt32(), (0x1000 - SyncptValuesOffset) / 8);

            IpcHandleDesc handleDesc = context.Request.HandleDesc;

            if (handleDesc == null || handleDesc.ToCopy.Length < 2)
            {
                Logger.Warning?.Print(LogClass.ServiceAm, "nx2:gsv 204 missing transfer memory handles");
                return ResultCode.InvalidParameters;
            }

            KTransferMemory rw = context.Process.HandleTable.GetObject<KTransferMemory>(handleDesc.ToCopy[0]);
            KTransferMemory ro = context.Process.HandleTable.GetObject<KTransferMemory>(handleDesc.ToCopy[1]);

            if (rw == null || ro == null || ro.Size < SyncptPageSize)
            {
                Logger.Warning?.Print(LogClass.ServiceAm,
                    $"nx2:gsv 204 invalid syncpt memory rw={rw?.Size:X} ro={ro?.Size:X}");
                return ResultCode.InvalidParameters;
            }

            // The server owns the RO page through the creator's address space.
            IVirtualMemoryManager memory = ro.Creator.CpuMemory;

            memory.Write(ro.Address + 0x00, 1u); // version
            memory.Write(ro.Address + 0x04, 0u); // base id
            memory.Write(ro.Address + 0x08, maxSyncpoints);
            memory.Write(ro.Address + 0x0c, SyncptValuesOffset);
            memory.Write(ro.Address + 0x10, SyncptRwSummaryOffset);
            memory.Write(ro.Address + 0x14, SyncptRoSummaryOffset);
            memory.Write(ro.Address + 0x18, SyncptRwBitsOffset);
            memory.Write(ro.Address + 0x1c, SyncptRoBitsOffset);

            KEvent requestEvent = new(context.Device.System.KernelContext);
            KEvent notifyEvent = new(context.Device.System.KernelContext);

            lock (_syncptLock)
            {
                _syncptMemory = memory;
                _syncptHost = context.Device.System.HostSyncpoint;
                _syncptRw = rw.Address;
                _syncptRo = ro.Address;
                _syncptCount = maxSyncpoints;
                _syncptNotify = notifyEvent;
                _syncptFaulted = false;
                _syncptTimer ??= new System.Threading.Timer(_ => PollSyncpts(), null, 0, 1);
            }

            if (context.Process.HandleTable.GenerateHandle(requestEvent.WritableEvent, out int requestHandle) != Result.Success ||
                context.Process.HandleTable.GenerateHandle(notifyEvent.ReadableEvent, out int notifyHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            Logger.Warning?.Print(LogClass.ServiceAm,
                $"nx2:gsv 204 version={versionRequested} max_syncpoints={maxSyncpoints} rw=0x{rw.Address:X} ro=0x{ro.Address:X}");

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(requestHandle, notifyHandle);
            context.ResponseData.BaseStream.Seek(4, System.IO.SeekOrigin.Begin);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        [CommandTipc(1402)]
        public ResultCode OpenNvsrvSession(ServiceCtx context)
        {
            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1")
            {
                var t = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();
                Ryujinx.Common.Logging.Logger.Warning?.Print(LogClass.ServiceAm, $"NX2 gsv1402 caller={t?.Owner.Pid} tid={t?.ThreadUid}");
            }
            MakeSessionObject(context, new INvDrvServices(context, nvsrv: true));

            return ResultCode.Success;
        }
    }
}
