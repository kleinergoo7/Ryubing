using Ryujinx.HLE.HOS.Services.Nv;
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

            return ResultCode.Success;
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

        [CommandTipc(429)]
        public ResultCode CreateNativeImage(ServiceCtx context)
        {
            uint[] preference = ReadPreference(context);
            uint count = Math.Max(context.RequestData.ReadUInt32(), 1);
            uint memory = context.RequestData.ReadUInt32();
            (_, _, _, _, ulong size) = Nx2NativeObjects.Layout(preference);

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

        [CommandTipc(115)]
        public ResultCode Unknown115(ServiceCtx context)
        {
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
        public ResultCode Stub(ServiceCtx context)
        {
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

        [CommandTipc(204)]
        public ResultCode AttachSyncptMemory(ServiceCtx context)
        {
            return ResultCode.Success;
        }

        [CommandTipc(1402)]
        public ResultCode OpenNvsrvSession(ServiceCtx context)
        {
            MakeSessionObject(context, new INvDrvServices(context, nvsrv: true));

            return ResultCode.Success;
        }
    }
}
