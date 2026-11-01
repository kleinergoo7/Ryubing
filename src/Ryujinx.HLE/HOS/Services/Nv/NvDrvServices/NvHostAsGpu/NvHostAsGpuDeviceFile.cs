using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu.Types;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu
{
    class NvHostAsGpuDeviceFile : NvDeviceFile
    {
        private const uint SmallPageSize = 0x1000;
        private const uint BigPageSize = 0x10000;

        private static readonly uint[] _pageSizes = [SmallPageSize, BigPageSize];

        private const ulong SmallRegionLimit = 0x400000000UL; // 16 GiB
        private const ulong DefaultUserSize = 1UL << 37;

        private readonly struct VmRegion
        {
            public ulong Start { get; }
            public ulong Limit { get; }

            public VmRegion(ulong start, ulong limit)
            {
                Start = start;
                Limit = limit;
            }
        }

        private static readonly VmRegion[] _vmRegions =
        [
            new((ulong)BigPageSize << 16, SmallRegionLimit),
            new(SmallRegionLimit, DefaultUserSize)
        ];

        private const ulong Nx2DefaultVaSplit = 0xC000000000UL;
        private const ulong Nx2ExtraBigPageSize = 4UL << 30;

        private readonly AddressSpaceContext _asContext;
        private readonly NvMemoryAllocator _memoryAllocator;

        private ulong _vaStart;
        private ulong _vaSplit = Nx2DefaultVaSplit;
        private ulong _vaEnd = Nx2DefaultVaSplit + Nx2ExtraBigPageSize;
        private uint _bigPageSize = BigPageSize;

        public NvHostAsGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            if (NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                context.Device.Gpu.IsNx2Ampere = true;
            }

            _asContext = new AddressSpaceContext(context.Device.Gpu.CreateMemoryManager(owner, context.Device.Memory.Size));
            _memoryAllocator = new NvMemoryAllocator();
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuAsMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = CallIoctlMethod<BindChannelArguments>(BindChannel, arguments);
                        break;
                    case 0x02:
                        result = CallIoctlMethod<AllocSpaceArguments>(AllocSpace, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<FreeSpaceArguments>(FreeSpace, arguments);
                        break;
                    case 0x05:
                        result = CallIoctlMethod<UnmapBufferArguments>(UnmapBuffer, arguments);
                        break;
                    case 0x06:
                        result = CallIoctlMethod<MapBufferExArguments>(MapBufferEx, arguments);
                        break;
                    case 0x08:
                        result = CallIoctlMethod<GetVaRegionsArguments>(GetVaRegions, arguments);
                        break;
                    case 0x09:
                        result = CallIoctlMethod<InitializeExArguments>(InitializeEx, arguments);
                        break;
                    case 0x0B:
                        if (NvHostCtrlGpuDeviceFile.Nx2Driver)
                        {
                            result = CallIoctlMethod<Nx2SyncpointShimInfo>(Nx2GetSyncpointShimInfo, arguments);
                        }
                        break;
                    case 0x14:
                        result = CallIoctlMethod<RemapArguments>(Remap, arguments);
                        break;
                }
            }

            return result;
        }

        public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvGpuAsMagic)
            {
                switch (command.Number)
                {
                    case 0x08:
                        // This is the same as the one in ioctl as inlineOutBuffer is empty.
                        result = CallIoctlMethod<GetVaRegionsArguments>(GetVaRegions, arguments);
                        break;
                }
            }

            return result;
        }

        private NvInternalResult BindChannel(ref BindChannelArguments arguments)
        {
            NvHostChannelDeviceFile channelDeviceFile = INvDrvServices.DeviceFileIdRegistry.GetData<NvHostChannelDeviceFile>(arguments.Fd);
            if (channelDeviceFile == null)
            {
                // TODO: Return invalid Fd error.
            }

            channelDeviceFile.Channel.BindMemory(_asContext.Gmm);

            return NvInternalResult.Success;
        }

        private NvInternalResult AllocSpace(ref AllocSpaceArguments arguments)
        {
            ulong size = (ulong)arguments.Pages * (ulong)arguments.PageSize;

            NvInternalResult result = NvInternalResult.Success;

            lock (_asContext)
            {
                // Note: When the fixed offset flag is not set,
                // the Offset field holds the alignment size instead.
                if ((arguments.Flags & AddressSpaceFlags.FixedOffset) != 0)
                {
                    bool regionInUse = _memoryAllocator.IsRegionInUse(arguments.Offset, size, out ulong freeAddressStartPosition);
                    ulong address;

                    if (!regionInUse)
                    {
                        _memoryAllocator.AllocateRange(arguments.Offset, size, freeAddressStartPosition);
                        address = freeAddressStartPosition;
                    }
                    else
                    {
                        address = NvMemoryAllocator.PteUnmapped;
                    }

                    arguments.Offset = address;
                }
                else
                {
                    ulong address = _memoryAllocator.GetFreeAddress(size, out ulong freeAddressStartPosition, arguments.Offset);
                    if (address != NvMemoryAllocator.PteUnmapped)
                    {
                        _memoryAllocator.AllocateRange(address, size, freeAddressStartPosition);
                    }

                    arguments.Offset = address;
                }

                if (arguments.Offset == NvMemoryAllocator.PteUnmapped)
                {
                    arguments.Offset = 0;

                    Logger.Warning?.Print(LogClass.ServiceNv, $"Failed to allocate size {size:x16}!");

                    result = NvInternalResult.OutOfMemory;
                }
                else
                {
                    _asContext.AddReservation(arguments.Offset, size);
                }
            }

            return result;
        }

        private NvInternalResult FreeSpace(ref FreeSpaceArguments arguments)
        {
            ulong size = (ulong)arguments.Pages * (ulong)arguments.PageSize;

            NvInternalResult result = NvInternalResult.Success;

            lock (_asContext)
            {
                if (_asContext.RemoveReservation(arguments.Offset))
                {
                    _memoryAllocator.DeallocateRange(arguments.Offset, size);
                    _asContext.Gmm.Unmap(arguments.Offset, size);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv,
                        $"Failed to free offset 0x{arguments.Offset:x16} size 0x{size:x16}!");

                    result = NvInternalResult.InvalidInput;
                }
            }

            return result;
        }

        private NvInternalResult UnmapBuffer(ref UnmapBufferArguments arguments)
        {
            lock (_asContext)
            {
                if (_asContext.RemoveMap(arguments.Offset, out ulong size))
                {
                    if (size != 0)
                    {
                        _memoryAllocator.DeallocateRange(arguments.Offset, size);
                        _asContext.Gmm.Unmap(arguments.Offset, size);
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid buffer offset {arguments.Offset:x16}!");
                }
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult MapBufferEx(ref MapBufferExArguments arguments)
        {
            const string MapErrorMsg = "Failed to map fixed buffer with offset 0x{0:x16}, size 0x{1:x16} and alignment 0x{2:x16}!";

            ulong physicalAddress;

            if ((arguments.Flags & AddressSpaceFlags.RemapSubRange) != 0)
            {
                lock (_asContext)
                {
                    if (_asContext.TryGetMapPhysicalAddress(arguments.Offset, out physicalAddress))
                    {
                        ulong virtualAddress = arguments.Offset + arguments.BufferOffset;

                        physicalAddress += arguments.BufferOffset;
                        _asContext.Gmm.Map(physicalAddress, virtualAddress, arguments.MappingSize, (PteKind)arguments.Kind);

                        return NvInternalResult.Success;
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.ServiceNv, $"Address 0x{arguments.Offset:x16} not mapped!");

                        return NvInternalResult.InvalidInput;
                    }
                }
            }

            NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, arguments.NvMapHandle);

            if (map == null)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid NvMap handle 0x{arguments.NvMapHandle:x8}!");

                return NvInternalResult.InvalidInput;
            }

            ulong pageSize = (ulong)arguments.PageSize;

            if (pageSize == 0)
            {
                pageSize = (ulong)map.Align;
            }

            physicalAddress = map.Address + arguments.BufferOffset;

            ulong size = arguments.MappingSize;

            if (size == 0)
            {
                size = map.Size;
            }

            NvInternalResult result = NvInternalResult.Success;

            lock (_asContext)
            {
                // Note: When the fixed offset flag is not set,
                // the Offset field holds the alignment size instead.
                bool virtualAddressAllocated = (arguments.Flags & AddressSpaceFlags.FixedOffset) == 0;

                if (!virtualAddressAllocated)
                {
                    if (_asContext.ValidateFixedBuffer(arguments.Offset, size, pageSize))
                    {
                        _asContext.Gmm.Map(physicalAddress, arguments.Offset, size, (PteKind)arguments.Kind);
                    }
                    else
                    {
                        string message = string.Format(MapErrorMsg, arguments.Offset, size, pageSize);

                        Logger.Warning?.Print(LogClass.ServiceNv, message);

                        result = NvInternalResult.InvalidInput;
                    }
                }
                else
                {
                    ulong va = _memoryAllocator.GetFreeAddress(size, out ulong freeAddressStartPosition, pageSize);
                    if (va != NvMemoryAllocator.PteUnmapped)
                    {
                        _memoryAllocator.AllocateRange(va, size, freeAddressStartPosition);
                    }

                    _asContext.Gmm.Map(physicalAddress, va, size, (PteKind)arguments.Kind);
                    arguments.Offset = va;
                }

                if (arguments.Offset == NvMemoryAllocator.PteUnmapped)
                {
                    arguments.Offset = 0;

                    Logger.Warning?.Print(LogClass.ServiceNv, $"Failed to map size 0x{size:x16}!");

                    result = NvInternalResult.InvalidInput;
                }
                else
                {
                    _asContext.AddMap(arguments.Offset, size, physicalAddress, virtualAddressAllocated);
                }
            }

            return result;
        }

        private NvInternalResult GetVaRegions(ref GetVaRegionsArguments arguments)
        {
            int vaRegionStructSize = Unsafe.SizeOf<VaRegion>();

            Debug.Assert(vaRegionStructSize == 0x18);
            Debug.Assert(_pageSizes.Length == 2);

            if (NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                Span<VaRegion> nx2Regions = arguments.Regions.AsSpan();

                nx2Regions[0] = new VaRegion
                {
                    Offset = _vaStart,
                    PageSize = SmallPageSize,
                    Pages = (_vaSplit - _vaStart) / SmallPageSize,
                };

                nx2Regions[1] = new VaRegion
                {
                    Offset = _vaSplit,
                    PageSize = _bigPageSize,
                    Pages = (_vaEnd - _vaSplit) / _bigPageSize,
                };

                arguments.BufferSize = (uint)(_pageSizes.Length * vaRegionStructSize);

                Logger.Info?.Print(LogClass.ServiceNv, $"as-gpu VA regions [0x{_vaStart:X}, 0x{_vaSplit:X}) big=0x{_bigPageSize:X} [0x{_vaSplit:X}, 0x{_vaEnd:X})");

                return NvInternalResult.Success;
            }

            uint writeEntries = (uint)(arguments.BufferSize / vaRegionStructSize);
            if (writeEntries > _pageSizes.Length)
            {
                writeEntries = (uint)_pageSizes.Length;
            }

            Span<VaRegion> regionsSpan = arguments.Regions.AsSpan();

            for (uint i = 0; i < writeEntries; i++)
            {
                ref VaRegion region = ref regionsSpan[(int)i];

                VmRegion vmRegion = _vmRegions[i];
                uint pageSize = _pageSizes[i];

                region.PageSize = pageSize;
                region.Offset = vmRegion.Start;
                region.Pages = (vmRegion.Limit - vmRegion.Start) / pageSize;
                region.Padding = 0;
            }

            arguments.BufferSize = (uint)(_pageSizes.Length * vaRegionStructSize);

            return NvInternalResult.Success;
        }

        private NvInternalResult InitializeEx(ref InitializeExArguments arguments)
        {
            if (NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                if (arguments.BigPageSize != 0)
                {
                    _bigPageSize = arguments.BigPageSize;
                }

                if (arguments.Unknown0 != 0)
                {
                    _vaStart = arguments.Unknown0;
                    _vaSplit = arguments.Unknown2;
                    _vaEnd = arguments.Unknown1;

                    if (_vaSplit == 0)
                    {
                        // Switch 2 NvRm asks for small pages over the whole range, then keeps a
                        // big-page region past the end so both allocators stay valid.
                        _vaSplit = _vaEnd;
                        _vaEnd += Nx2ExtraBigPageSize;
                    }
                }

                Logger.Info?.Print(LogClass.ServiceNv, $"as-gpu InitializeEx flags=0x{arguments.Flags:X} big_page=0x{_bigPageSize:X} VA [0x{_vaStart:X}, 0x{_vaSplit:X}, 0x{_vaEnd:X})");
            }

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult Nx2GetSyncpointShimInfo(ref Nx2SyncpointShimInfo arguments)
        {
            // The guest maps one page at the end of its GPU VA space and reads host1x syncpoint
            // counters through it. The GPU memory manager serves the values for that page.
            arguments.Address = _vaEnd - 0x1000;
            arguments.Stride = sizeof(uint);
            arguments.Count = 192;

            _asContext.Gmm.Nx2SyncpointShim = arguments.Address;
            _asContext.Gmm.Nx2SyncpointReader = id => Context.Device.System.HostSyncpoint.ReadSyncpointValue(id);

            Logger.Info?.Print(LogClass.ServiceNv, $"as-gpu syncpoint shim at 0x{arguments.Address:X} count={arguments.Count}");

            return NvInternalResult.Success;
        }

        private NvInternalResult Remap(Span<RemapArguments> arguments)
        {
            MemoryManager gmm = _asContext.Gmm;

            for (int index = 0; index < arguments.Length; index++)
            {
                ref RemapArguments argument = ref arguments[index];
                ulong gpuVa = (ulong)argument.GpuOffset << 16;
                ulong size = (ulong)argument.Pages << 16;
                int nvmapHandle = argument.NvMapHandle;

                if (nvmapHandle == 0)
                {
                    gmm.Unmap(gpuVa, size);
                }
                else
                {
                    ulong mapOffs = (ulong)argument.MapOffset << 16;
                    PteKind kind = (PteKind)argument.Kind;

                    NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, nvmapHandle);

                    if (map == null)
                    {
                        Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid NvMap handle 0x{nvmapHandle:x8}!");

                        return NvInternalResult.InvalidInput;
                    }

                    gmm.Map(mapOffs + map.Address, gpuVa, size, kind);
                }
            }

            return NvInternalResult.Success;
        }

        public override void Close() { }
    }
}
