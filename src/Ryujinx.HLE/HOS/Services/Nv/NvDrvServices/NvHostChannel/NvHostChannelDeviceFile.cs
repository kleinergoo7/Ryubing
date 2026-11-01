using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.Types;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrlGpu;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel
{
    class NvHostChannelDeviceFile : NvDeviceFile
    {
        private static readonly ConcurrentDictionary<ulong, Host1xContext> _host1xContextRegistry = new();

        private const uint MaxModuleSyncpoint = 16;

#pragma warning disable IDE0052 // Remove unread private member
        private uint _timeout;
        private uint _submitTimeout;
        private uint _timeslice;
#pragma warning restore IDE0052

        private readonly Switch _device;

        private readonly IVirtualMemoryManager _memory;
        private readonly Host1xContext _host1xContext;
        private readonly long _contextId;

        public GpuChannel Channel { get; }

        public enum ResourcePolicy
        {
            Device,
            Channel,
        }

        protected static uint[] DeviceSyncpoints = new uint[MaxModuleSyncpoint];

        protected uint[] ChannelSyncpoints;

        protected static ResourcePolicy ChannelResourcePolicy = ResourcePolicy.Device;

        private NvFence _channelSyncpoint;

        protected NvFence ChannelSyncpoint => _channelSyncpoint;

        private static int ProcLogCount;

        protected Switch HostDevice => _device;

        public NvHostChannelDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            _device = context.Device;
            _memory = memory;
            _timeout = 3000;
            _submitTimeout = 0;
            _timeslice = 0;
            _host1xContext = GetHost1XContext(context.Device.Gpu, owner);
            _contextId = _host1xContext.Host1x.CreateContext();
            Channel = _device.Gpu.CreateChannel();

            ChannelInitialization.InitializeState(Channel);

            ChannelSyncpoints = new uint[MaxModuleSyncpoint];

            _channelSyncpoint.Id = _device.System.HostSyncpoint.AllocateSyncpoint(false);
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvHostCustomMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = Submit(arguments);
                        break;
                    case 0x02:
                        result = CallIoctlMethod<GetParameterArguments>(GetSyncpoint, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<GetParameterArguments>(GetWaitBase, arguments);
                        break;
                    case 0x07:
                        result = CallIoctlMethod<uint>(SetSubmitTimeout, arguments);
                        break;
                    case 0x09:
                        result = MapCommandBuffer(arguments);
                        break;
                    case 0x0a:
                        result = UnmapCommandBuffer(arguments);
                        break;
                }
            }
            else if (command.Type == NvIoctl.NvHostMagic)
            {
                switch (command.Number)
                {
                    case 0x01:
                        result = CallIoctlMethod<int>(SetNvMapFd, arguments);
                        break;
                    case 0x03:
                        result = CallIoctlMethod<uint>(SetTimeout, arguments);
                        break;
                    case 0x08:
                        result = SubmitGpfifo(arguments);
                        break;
                    case 0x09:
                        result = CallIoctlMethod<AllocObjCtxArguments>(AllocObjCtx, arguments);
                        break;
                    case 0x0b:
                        result = CallIoctlMethod<ZcullBindArguments>(ZcullBind, arguments);
                        break;
                    case 0x0c:
                        result = CallIoctlMethod<SetErrorNotifierArguments>(SetErrorNotifier, arguments);
                        break;
                    case 0x0d:
                        result = CallIoctlMethod<NvChannelPriority>(SetPriority, arguments);
                        break;
                    case 0x18:
                        result = CallIoctlMethod<AllocGpfifoExArguments>(AllocGpfifoEx, arguments);
                        break;
                    case 0x1a:
                        result = CallIoctlMethod<AllocGpfifoExArguments>(AllocGpfifoEx2, arguments);
                        break;
                    case 0x1d:
                        result = CallIoctlMethod<uint>(SetTimeslice, arguments);
                        break;
                }
            }
            else if (command.Type == NvIoctl.NvGpuMagic)
            {
                switch (command.Number)
                {
                    case 0x14:
                        result = CallIoctlMethod<ulong>(SetUserData, arguments);
                        break;
                }
            }

            return result;
        }

        private NvInternalResult Submit(Span<byte> arguments)
        {
            SubmitArguments submitHeader = GetSpanAndSkip<SubmitArguments>(ref arguments, 1)[0];
            Span<CommandBuffer> commandBuffers = GetSpanAndSkip<CommandBuffer>(ref arguments, submitHeader.CmdBufsCount);
#pragma warning disable IDE0059 // Remove unnecessary value assignment
            Span<Reloc> relocs = GetSpanAndSkip<Reloc>(ref arguments, submitHeader.RelocsCount);
            Span<uint> relocShifts = GetSpanAndSkip<uint>(ref arguments, submitHeader.RelocsCount);
#pragma warning restore IDE0059
            Span<SyncptIncr> syncptIncrs = GetSpanAndSkip<SyncptIncr>(ref arguments, submitHeader.SyncptIncrsCount);
            Span<uint> fenceThresholds = GetSpanAndSkip<uint>(ref arguments, submitHeader.FencesCount);

            lock (_device)
            {
                for (int i = 0; i < syncptIncrs.Length; i++)
                {
                    SyncptIncr syncptIncr = syncptIncrs[i];

                    uint id = syncptIncr.Id;

                    fenceThresholds[i] = Context.Device.System.HostSyncpoint.IncrementSyncpointMax(id, syncptIncr.Incrs);
                }

                foreach (CommandBuffer commandBuffer in commandBuffers)
                {
                    NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBuffer.Mem);

                    ReadOnlySpan<byte> data = _memory.GetSpan(map.Address + commandBuffer.Offset, commandBuffer.WordsCount * 4);

                    _host1xContext.Host1x.Submit(MemoryMarshal.Cast<byte, int>(data), _contextId);
                }
            }

            return NvInternalResult.Success;
        }

        private Span<T> GetSpanAndSkip<T>(ref Span<byte> arguments, int count) where T : unmanaged
        {
            Span<T> output = MemoryMarshal.Cast<byte, T>(arguments)[..count];

            arguments = arguments[(Unsafe.SizeOf<T>() * count)..];

            return output;
        }

        private NvInternalResult GetSyncpoint(ref GetParameterArguments arguments)
        {
            if (arguments.Parameter >= MaxModuleSyncpoint)
            {
                return NvInternalResult.InvalidInput;
            }

            if (ChannelResourcePolicy == ResourcePolicy.Device)
            {
                arguments.Value = GetSyncpointDevice(_device.System.HostSyncpoint, arguments.Parameter, false);
            }
            else
            {
                arguments.Value = GetSyncpointChannel(arguments.Parameter, false);
            }

            if (arguments.Value == 0)
            {
                return NvInternalResult.TryAgain;
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult GetWaitBase(ref GetParameterArguments arguments)
        {
            arguments.Value = 0;

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SetSubmitTimeout(ref uint submitTimeout)
        {
            _submitTimeout = submitTimeout;

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult MapCommandBuffer(Span<byte> arguments)
        {
            int headerSize = Unsafe.SizeOf<MapCommandBufferArguments>();
            MapCommandBufferArguments commandBufferHeader = MemoryMarshal.Cast<byte, MapCommandBufferArguments>(arguments)[0];
            Span<CommandBufferHandle> commandBufferEntries = MemoryMarshal.Cast<byte, CommandBufferHandle>(arguments[headerSize..])[..commandBufferHeader.NumEntries];

            foreach (ref CommandBufferHandle commandBufferEntry in commandBufferEntries)
            {
                NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBufferEntry.MapHandle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{commandBufferEntry.MapHandle:x8}!");

                    return NvInternalResult.InvalidInput;
                }

                lock (map)
                {
                    if (map.DmaMapAddress == 0)
                    {
                        ulong va = _host1xContext.MemoryAllocator.GetFreeAddress(map.Size, out ulong freeAddressStartPosition, 1, MemoryManager.PageSize);

                        if (va != NvMemoryAllocator.PteUnmapped && va <= uint.MaxValue && (va + map.Size) <= uint.MaxValue)
                        {
                            _host1xContext.MemoryAllocator.AllocateRange(va, map.Size, freeAddressStartPosition);
                            _host1xContext.Smmu.Map(map.Address, va, map.Size);
                            map.DmaMapAddress = va;
                        }
                        else
                        {
                            map.DmaMapAddress = NvMemoryAllocator.PteUnmapped;
                        }
                    }

                    commandBufferEntry.MapAddress = (int)map.DmaMapAddress;
                }
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult UnmapCommandBuffer(Span<byte> arguments)
        {
            int headerSize = Unsafe.SizeOf<MapCommandBufferArguments>();
            MapCommandBufferArguments commandBufferHeader = MemoryMarshal.Cast<byte, MapCommandBufferArguments>(arguments)[0];
            Span<CommandBufferHandle> commandBufferEntries = MemoryMarshal.Cast<byte, CommandBufferHandle>(arguments[headerSize..])[..commandBufferHeader.NumEntries];

            foreach (ref CommandBufferHandle commandBufferEntry in commandBufferEntries)
            {
                NvMapHandle map = NvMapDeviceFile.GetMapFromHandle(Owner, commandBufferEntry.MapHandle);

                if (map == null)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid handle 0x{commandBufferEntry.MapHandle:x8}!");

                    return NvInternalResult.InvalidInput;
                }

                lock (map)
                {
                    if (map.DmaMapAddress != 0)
                    {
                        // FIXME:
                        // To make unmapping work, we need separate address space per channel.
                        // Right now NVDEC and VIC share the GPU address space which is not correct at all.

                        // _host1xContext.MemoryAllocator.Free((ulong)map.DmaMapAddress, (uint)map.Size);

                        // map.DmaMapAddress = 0;
                    }
                }
            }

            return NvInternalResult.Success;
        }

        private NvInternalResult SetNvMapFd(ref int nvMapFd)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SetTimeout(ref uint timeout)
        {
            _timeout = timeout;

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SubmitGpfifo(Span<byte> arguments)
        {
            int headerSize = Unsafe.SizeOf<SubmitGpfifoArguments>();
            SubmitGpfifoArguments gpfifoSubmissionHeader = MemoryMarshal.Cast<byte, SubmitGpfifoArguments>(arguments)[0];
            Span<ulong> gpfifoEntries = MemoryMarshal.Cast<byte, ulong>(arguments[headerSize..])[..gpfifoSubmissionHeader.NumEntries];

            return SubmitGpfifo(ref gpfifoSubmissionHeader, gpfifoEntries);
        }

        private NvInternalResult AllocObjCtx(ref AllocObjCtxArguments arguments)
        {
            if (NvHostCtrlGpuDeviceFile.Nx2Driver)
            {
                // Switch 2 NvRm asks for Ampere classes, or class 0 (it reads the class numbers
                // from a characteristics layout we don't know yet). Map them onto the Maxwell
                // engine slots this emulator knows.
                uint mapped = arguments.ClassNumber switch
                {
                    0xC797 or 0xC697 or 0xC597 => 0xB197, // 3D
                    0xC7C0 or 0xC6C0 or 0xC5C0 => 0xB1C0, // compute
                    0xC7B5 or 0xC6B5 or 0xC5B5 => 0xB0B5, // DMA copy
                    0xC56F or 0xC76F => 0xB06F, // channel GPFIFO
                    0 => 0xB197, // first free engine slot
                    _ => arguments.ClassNumber,
                };

                if (mapped != arguments.ClassNumber)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"Switch 2 object class 0x{arguments.ClassNumber:X} -> 0x{mapped:X}");

                    arguments.ClassNumber = mapped;
                }
            }

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult ZcullBind(ref ZcullBindArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SetErrorNotifier(ref SetErrorNotifierArguments arguments)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SetPriority(ref NvChannelPriority priority)
        {
            switch (priority)
            {
                case NvChannelPriority.Low:
                    _timeslice = 1300; // Timeslice low priority in micro-seconds
                    break;
                case NvChannelPriority.Medium:
                    _timeslice = 2600; // Timeslice medium priority in micro-seconds
                    break;
                case NvChannelPriority.High:
                    _timeslice = 5200; // Timeslice high priority in micro-seconds
                    break;
                default:
                    return NvInternalResult.InvalidInput;
            }

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            // TODO: disable and preempt channel when GPU scheduler will be implemented.

            return NvInternalResult.Success;
        }

        private NvInternalResult AllocGpfifoEx(ref AllocGpfifoExArguments arguments)
        {
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);

            arguments.Fence = _channelSyncpoint;

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult AllocGpfifoEx2(ref AllocGpfifoExArguments arguments)
        {
            _channelSyncpoint.UpdateValue(_device.System.HostSyncpoint);

            arguments.Fence = _channelSyncpoint;

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        private NvInternalResult SetTimeslice(ref uint timeslice)
        {
            if (timeslice is < 1000 or > 50000)
            {
                return NvInternalResult.InvalidInput;
            }

            _timeslice = timeslice; // in micro-seconds

            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            // TODO: disable and preempt channel when GPU scheduler will be implemented.

            return NvInternalResult.Success;
        }

        private NvInternalResult SetUserData(ref ulong userData)
        {
            Logger.Stub?.PrintStub(LogClass.ServiceNv);

            return NvInternalResult.Success;
        }

        protected NvInternalResult SubmitGpfifo(ref SubmitGpfifoArguments header, Span<ulong> entries)
        {
            bool nx2 = NvHostCtrlGpuDeviceFile.Nx2Driver;

            // WORKAROUND: Switch 2 nvhost allows waiting on a fence and incrementing in one
            // submission; older (Switch 1) nvhost rejects that combination. Accepted on faith
            // from observed guest traffic; re-check against hardware behavior if submits misbehave.
            if (!nx2 &&
                header.Flags.HasFlag(SubmitGpfifoFlags.FenceWait) &&
                header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
            {
                return NvInternalResult.InvalidInput;
            }

            if (nx2)
            {
                System.Threading.Interlocked.Increment(ref Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy.Nx2Counters.Submits);
            }

            Ryujinx.HLE.HOS.Kernel.Threading.KThread caller = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();
            ulong callerPc = caller?.Context.Pc ?? 0;
            string callerTrace = caller?.GetGuestStackTrace() ?? string.Empty;

            Logger.Warning?.Print(LogClass.ServiceNv, $"NX2 submit from thread {caller?.ThreadUid} pc=0x{callerPc:X}:\n{callerTrace}");

            // The sender thread is blocked in SendSyncRequest right now; find it by its stack.
            // Server-loop threads have no guest stack. Senders park with PC at the send
            // SVC site (observed as 0xC38408C); match that first, then any parked thread
            // whose stack passes through the IPC send wrapper region.
            // NOTE: stack walking can fail (PAC-signed returns), so also log every thread
            // PC of the game process with no unwinding; an empty result here is a tool
            // failure, not proof of absence.
            int senderLogs = 0;
            int emptyStacks = 0;
            System.Text.StringBuilder pcLine = new("NX2 pcs pid=109:");

            foreach (System.Collections.Generic.KeyValuePair<ulong, Ryujinx.HLE.HOS.Kernel.Process.KProcess> procEntry in _device.System.KernelContext.Processes)
            {
                if (procEntry.Key != 109)
                {
                    continue;
                }

                foreach (Ryujinx.HLE.HOS.Kernel.Threading.KThread thread in procEntry.Value.DebugThreads)
                {
                    pcLine.Append($" {thread.ThreadUid}=0x{thread.Context.Pc:X}");

                    string trace = thread.GetGuestStackTrace();

                    if (string.IsNullOrWhiteSpace(trace))
                    {
                        emptyStacks++;

                        continue;
                    }

                    if (trace.Length >= 4000)
                    {
                        continue;
                    }

                    bool parkedAtSend = thread.Context.Pc == 0xC38408C;
                    bool viaWrapper = trace.Contains("c3840", System.StringComparison.OrdinalIgnoreCase);

                    if (!parkedAtSend && !viaWrapper)
                    {
                        continue;
                    }

                    Logger.Warning?.Print(LogClass.ServiceNv, $"NX2 submitter pid={procEntry.Key} tid={thread.ThreadUid} pc=0x{thread.Context.Pc:X}:\n{trace}");

                    if (++senderLogs >= 4)
                    {
                        break;
                    }
                }

                if (senderLogs >= 4)
                {
                    break;
                }
            }

            if (senderLogs == 0)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, $"NX2 submitter: no parked sender thread found ({emptyStacks} empty stacks). {pcLine}");
            }

            if (System.Threading.Interlocked.Increment(ref ProcLogCount) == 1)
            {
                foreach (System.Collections.Generic.KeyValuePair<ulong, Ryujinx.HLE.HOS.Kernel.Process.KProcess> entry in _device.System.KernelContext.Processes)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"NX2 proc pid={entry.Key} name={entry.Value.Name} threads={string.Join(',', System.Linq.Enumerable.Select(entry.Value.DebugThreads, t => t.ThreadUid.ToString()))}");
                }
            }

            Logger.Info?.Print(LogClass.ServiceNv, $"SubmitGpfifo flags={header.Flags} fence=({header.Fence.Id}, {header.Fence.Value}) entries=0x{entries.Length:X} address=0x{header.Address:X} first=0x{(entries.Length > 0 ? entries[0] : 0):X} nx2={nx2} pc=0x{callerPc:X}");

            if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceWait) && !_device.System.HostSyncpoint.IsSyncpointExpired(header.Fence.Id, header.Fence.Value))
            {
                Channel.PushHostCommandBuffer(CreateWaitCommandBuffer(header.Fence));
            }

            header.Fence.Id = _channelSyncpoint.Id;

            uint incrementCount = 0;

            if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement) || header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
            {
                incrementCount = header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement) ? 2u : 0u;

                if (header.Flags.HasFlag(SubmitGpfifoFlags.IncrementWithValue))
                {
                    incrementCount += header.Fence.Value;
                }

                header.Fence.Value = _device.System.HostSyncpoint.IncrementSyncpointMaxExt(header.Fence.Id, (int)incrementCount);
            }
            else
            {
                header.Fence.Value = _device.System.HostSyncpoint.ReadSyncpointMaxValue(header.Fence.Id);
            }

            if (nx2)
            {
                // Switch 2 GPFIFO entries are 64-bit command list headers: bits 0..39 GPU
                // address, bits 42..62 size in words, bit 63 sync. Convert them to the Maxwell
                // GPEntry the emulator's FIFO understands.
                ulong[] translated = new ulong[entries.Length];

                for (int i = 0; i < entries.Length; i++)
                {
                    ulong raw = entries[i];

                    Logger.Info?.Print(LogClass.ServiceNv, $"NX2 entry[{i}]=0x{raw:X16} addr=0x{raw & 0xFFFFFFFFFFUL:X} len=0x{(raw >> 42) & 0x1F_FFFF:X}");
                    ulong address = raw & 0xFF_FFFF_FFFFUL;
                    uint length = (uint)((raw >> 42) & 0x1F_FFFF);
                    uint sync = (uint)(raw >> 63);

                    translated[i] = (address & 0xFFFF_FFFCUL) |
                                    ((((address >> 32) & 0xFF) |
                                      ((ulong)length << 10) |
                                      ((ulong)sync << 31)) << 32);
                }

                Channel.PushEntries(translated);
            }
            else
            {
                Channel.PushEntries(entries);
            }

            if (header.Flags.HasFlag(SubmitGpfifoFlags.FenceIncrement))
            {
                Channel.PushHostCommandBuffer(CreateIncrementCommandBuffer(ref header.Fence, header.Flags));
            }
            else if (nx2 && incrementCount > 0)
            {
                // Switch 2 pushbuffers signal completion with semaphore releases, never host1x
                // syncpoint methods, but the driver still raises the syncpoint max by the
                // submission's increment value and waits for it. Append the increments.
                Channel.PushHostCommandBuffer(CreateIncrementCountCommandBuffer(ref header.Fence, incrementCount));
            }

            header.Flags = SubmitGpfifoFlags.None;

            _device.Gpu.GPFifo.SignalNewEntries();

            OnSubmission();

            return NvInternalResult.Success;
        }

        /// <summary>
        /// Called after a GPFIFO submission was queued, so Switch 2 paths can report completion
        /// to the guest's event pool.
        /// </summary>
        protected virtual void OnSubmission() { }

        public uint GetSyncpointChannel(uint index, bool isClientManaged)
        {
            if (ChannelSyncpoints[index] != 0)
            {
                return ChannelSyncpoints[index];
            }

            ChannelSyncpoints[index] = _device.System.HostSyncpoint.AllocateSyncpoint(isClientManaged);

            return ChannelSyncpoints[index];
        }

        public uint GetSyncpointDevice(NvHostSyncpt syncpointManager, uint index, bool isClientManaged)
        {
            if (DeviceSyncpoints[index] != 0)
            {
                return DeviceSyncpoints[index];
            }

            DeviceSyncpoints[index] = syncpointManager.AllocateSyncpoint(isClientManaged);

            return DeviceSyncpoints[index];
        }

        private static int[] CreateWaitCommandBuffer(NvFence fence)
        {
            int[] commandBuffer = new int[4];

            // SyncpointValue = fence.Value;
            commandBuffer[0] = 0x2001001C;
            commandBuffer[1] = (int)fence.Value;

            // SyncpointAction(fence.id, increment: false, switch_en: true);
            commandBuffer[2] = 0x2001001D;
            commandBuffer[3] = (((int)fence.Id << 8) | (0 << 0) | (1 << 4));

            return commandBuffer;
        }

        private int[] CreateIncrementCommandBuffer(ref NvFence fence, SubmitGpfifoFlags flags)
        {
            bool hasWfi = !flags.HasFlag(SubmitGpfifoFlags.SuppressWfi);

            int[] commandBuffer;

            int offset = 0;

            if (hasWfi)
            {
                commandBuffer = new int[8];

                // WaitForInterrupt(handle)
                commandBuffer[offset++] = 0x2001001E;
                commandBuffer[offset++] = 0x0;
            }
            else
            {
                commandBuffer = new int[6];
            }

            // SyncpointValue = 0x0;
            commandBuffer[offset++] = 0x2001001C;
            commandBuffer[offset++] = 0x0;

            // Increment the syncpoint 2 times. (mitigate a hardware bug)

            // SyncpointAction(fence.id, increment: true, switch_en: false);
            commandBuffer[offset++] = 0x2001001D;
            commandBuffer[offset++] = (((int)fence.Id << 8) | (1 << 0) | (0 << 4));

            // SyncpointAction(fence.id, increment: true, switch_en: false);
            commandBuffer[offset++] = 0x2001001D;
            commandBuffer[offset++] = (((int)fence.Id << 8) | (1 << 0) | (0 << 4));

            return commandBuffer;
        }

        private int[] CreateIncrementCountCommandBuffer(ref NvFence fence, uint count)
        {
            int[] commandBuffer = new int[4 + (int)count * 2];

            int offset = 0;

            // WaitForIdle(handle)
            commandBuffer[offset++] = 0x2001001E;
            commandBuffer[offset++] = 0x0;

            // SyncpointValue = 0x0;
            commandBuffer[offset++] = 0x2001001C;
            commandBuffer[offset++] = 0x0;

            for (uint i = 0; i < count; i++)
            {
                // SyncpointAction(fence.id, increment: true, switch_en: false);
                commandBuffer[offset++] = 0x2001001D;
                commandBuffer[offset++] = (((int)fence.Id << 8) | (1 << 0) | (0 << 4));
            }

            return commandBuffer;
        }

        public override void Close()
        {
            _host1xContext.Host1x.DestroyContext(_contextId);
            Channel.Dispose();

            for (int i = 0; i < MaxModuleSyncpoint; i++)
            {
                if (ChannelSyncpoints[i] != 0)
                {
                    _device.System.HostSyncpoint.ReleaseSyncpoint(ChannelSyncpoints[i]);
                    ChannelSyncpoints[i] = 0;
                }
            }

            _device.System.HostSyncpoint.ReleaseSyncpoint(_channelSyncpoint.Id);
            _channelSyncpoint.Id = 0;
        }

        private static Host1xContext GetHost1XContext(GpuContext gpu, ulong pid)
        {
            return _host1xContextRegistry.GetOrAdd(pid, key => new Host1xContext(gpu, key));
        }

        public static void Destroy()
        {
            foreach (Host1xContext host1xContext in _host1xContextRegistry.Values)
            {
                host1xContext.Dispose();
            }

            _host1xContextRegistry.Clear();
        }
    }
}
