using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Represents a compute engine class.
    /// </summary>
    class ComputeClass : IDeviceState
    {
        private const int SendPcasBMethodOffset = 0xae * 4;
        private const int SendSignalingPcasBMethodOffset = 0xaf * 4;
        private const int SendSignalingPcasBNewMethodOffset = 0xb0 * 4;

        private const int KeepMmeCodeMethodOffset = 0x45 * 4;
        private const int AddMmeCodeMethodOffset = 0x46 * 4;
        private const int SetMmeBindPointerMethodOffset = 0x47 * 4;
        private const int BindMmeStartMethodOffset = 0x48 * 4;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;
        private readonly ThreedClass _3dEngine;
        private readonly GPFifoClass _fifoClass;
        private readonly DeviceState<ComputeClassState> _state;

        private readonly InlineToMemoryClass _i2mClass;

        private readonly QmdScheduler _qmdScheduler = new();

        private static int _qmdFailureLogs;

        /// <summary>
        /// Creates a new instance of the compute engine class.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">GPU channel</param>
        /// <param name="threedEngine">3D engine</param>
        /// <param name="fifoClass">GP FIFO class that owns the macro code banks</param>
        public ComputeClass(GpuContext context, GpuChannel channel, ThreedClass threedEngine, GPFifoClass fifoClass)
        {
            _context = context;
            _channel = channel;
            _3dEngine = threedEngine;
            _fifoClass = fifoClass;
            _state = new DeviceState<ComputeClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(ComputeClassState.LaunchDma), new RwCallback(LaunchDma, null) },
                { nameof(ComputeClassState.LoadInlineData), new RwCallback(LoadInlineData, null) },
                { nameof(ComputeClassState.SendSignalingPcasB), new RwCallback(SendSignalingPcasB, null) },
                { nameof(ComputeClassState.SetReportSemaphoreD), new RwCallback(ReportSemaphore, null) },
            });

            _i2mClass = new InlineToMemoryClass(context, channel, initializeState: false);
        }

        /// <summary>
        /// Reads data from the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <returns>Data at the specified offset</returns>
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// Writes data to the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <param name="data">Data to be written</param>
        public void Write(int offset, int data)
        {
            _state.Write(offset, data);

            if (_context.IsNx2Ampere)
            {
                // Switch 2 compute streams upload their macro code through this class instead of
                // the GPFIFO class; the code bank lives with the channel's other macro state.
                switch (offset)
                {
                    case KeepMmeCodeMethodOffset:
                        _fifoClass.KeepComputeMmeCode(data);
                        break;
                    case AddMmeCodeMethodOffset:
                        _fifoClass.AddComputeMmeCode(data);
                        break;
                    case SetMmeBindPointerMethodOffset:
                        _fifoClass.SetComputeMmeBindPointer(data);
                        break;
                    case BindMmeStartMethodOffset:
                        _fifoClass.BindComputeMmeStart(data);
                        break;
                }

                if (offset == SendPcasBMethodOffset ||
                    offset == SendSignalingPcasBMethodOffset ||
                    offset == SendSignalingPcasBNewMethodOffset)
                {
                    ProcessPcas((uint)data);
                }
            }
        }

        /// <summary>
        /// Launches the Inline-to-Memory DMA copy operation.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LaunchDma(int argument)
        {
            _i2mClass.LaunchDma(ref Unsafe.As<ComputeClassState, InlineToMemoryClassState>(ref _state.State), argument);
        }

        /// <summary>
        /// Pushes a block of data to the Inline-to-Memory engine.
        /// </summary>
        /// <param name="data">Data to push</param>
        public void LoadInlineData(ReadOnlySpan<int> data)
        {
            _i2mClass.LoadInlineData(data);
        }

        /// <summary>
        /// Pushes a word of data to the Inline-to-Memory engine.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LoadInlineData(int argument)
        {
            _i2mClass.LoadInlineData(argument);
        }

        /// <summary>
        /// Writes a compute queue release into guest memory.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void ReportSemaphore(int argument)
        {
            if (!_context.IsNx2Ampere)
            {
                return;
            }

            ulong address = ((ulong)(_state.State.SetReportSemaphoreA & 0x1ffffff) << 32) |
                             _state.State.SetReportSemaphoreB;

            uint payload = _state.State.SetReportSemaphoreC;
            uint operation = (uint)argument & 3;

            if (operation == 1)
            {
                // Acquire waits are handled by the puller's semaphore path.
                return;
            }

            if (operation == 3)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Compute semaphore trap at {address:x16} ignored.");
                return;
            }

            if ((argument & 8) != 0)
            {
                uint old = _channel.MemoryManager.Read<uint>(address);
                uint value = (((uint)argument >> 9) & 7) switch
                {
                    0 => old + payload,
                    1 => Math.Min(old, payload),
                    2 => Math.Max(old, payload),
                    3 => old >= payload ? 0 : old + 1,
                    4 => old == 0 || old > payload ? payload : old - 1,
                    5 => old & payload,
                    6 => old | payload,
                    _ => old ^ payload,
                };

                _channel.MemoryManager.Write(address, value);
                return;
            }

            _channel.MemoryManager.Write(address, payload);

            if ((argument & (1u << 28)) == 0)
            {
                // Four-word release: clear the padding word and store the timestamp.
                _channel.MemoryManager.Write(address + 4, 0u);
                _channel.MemoryManager.Write(address + 8, _context.GetTimestamp());
            }
        }

        /// <summary>
        /// Performs the compute dispatch operation.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void SendSignalingPcasB(int argument)
        {
            if (_context.IsNx2Ampere)
            {
                // Switch 2 QMD 3.0 streams are scheduled by ProcessPcas.
                return;
            }

            MemoryManager memoryManager = _channel.MemoryManager;

            // Since we're going to change the state, make sure any pending instanced draws are done.
            _3dEngine.PerformDeferredDraws();

            // Make sure all pending uniform buffer data is written to memory.
            _3dEngine.FlushUboDirty();

            uint qmdAddress = _state.State.SendPcasA;

            ComputeQmd qmd = _channel.MemoryManager.Read<ComputeQmd>((ulong)qmdAddress << 8);

            ulong shaderGpuVa = ((ulong)_state.State.SetProgramRegionAAddressUpper << 32) | _state.State.SetProgramRegionB;

            shaderGpuVa += (uint)qmd.ProgramOffset;

            int localMemorySize = qmd.ShaderLocalMemoryLowSize + qmd.ShaderLocalMemoryHighSize;

            int sharedMemorySize = Math.Min(qmd.SharedMemorySize, _context.Capabilities.MaximumComputeSharedMemorySize);

            bool[] constantBufferValid = new bool[Constants.TotalCpUniformBuffers];
            ulong[] constantBufferAddress = new ulong[Constants.TotalCpUniformBuffers];
            ulong[] constantBufferSize = new ulong[Constants.TotalCpUniformBuffers];

            for (int index = 0; index < Constants.TotalCpUniformBuffers; index++)
            {
                constantBufferValid[index] = qmd.ConstantBufferValid(index);
                constantBufferAddress[index] = (uint)qmd.ConstantBufferAddrLower(index) |
                    (ulong)qmd.ConstantBufferAddrUpper(index) << 32;
                constantBufferSize[index] = (ulong)qmd.ConstantBufferSize(index);
            }

            Dispatch(
                shaderGpuVa,
                qmd.CtaThreadDimension0,
                qmd.CtaThreadDimension1,
                qmd.CtaThreadDimension2,
                localMemorySize,
                sharedMemorySize,
                qmd.SamplerIndex,
                constantBufferValid,
                constantBufferAddress,
                constantBufferSize);
        }

        /// <summary>
        /// Applies a Switch 2 QMD 3.0 PCAS action.
        /// </summary>
        /// <param name="argument">PCAS control word</param>
        private void ProcessPcas(uint argument)
        {
            _3dEngine.PerformDeferredDraws();
            _3dEngine.FlushUboDirty();

            ulong qmdAddress = (ulong)_state.State.SendPcasA << 8;

            string error = _qmdScheduler.Signal(
                qmdAddress,
                argument,
                ReadQmd,
                ReleaseQmd,
                DispatchQmd);

            if (error != null && _qmdFailureLogs++ < 32)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"QMD {qmdAddress:x16} (control {argument:x}) failed: {error}.");
            }
        }

        /// <summary>
        /// Reads a QMD 3.0 descriptor from guest memory.
        /// </summary>
        private bool ReadQmd(ulong address, Span<int> descriptor)
        {
            try
            {
                ReadOnlySpan<byte> data = _channel.MemoryManager.GetSpan(address, descriptor.Length * 4, false);

                if (data.Length < descriptor.Length * 4)
                {
                    return false;
                }

                for (int index = 0; index < descriptor.Length; index++)
                {
                    descriptor[index] = BitConverter.ToInt32(data.Slice(index * 4, 4));
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes a QMD release into guest memory.
        /// </summary>
        private bool ReleaseQmd(ulong address, uint payload, uint flags)
        {
            bool add = (flags & (1u << 26)) != 0;
            bool max = (flags & (1u << 19)) != 0;

            try
            {
                if (add)
                {
                    uint value = _channel.MemoryManager.Read<uint>(address) + payload;
                    _channel.MemoryManager.Write(address, value);
                }
                else if (max)
                {
                    // Completion counters are also advanced by other releases; a plain write would
                    // move them backwards and leave greater-or-equal acquires waiting forever.
                    uint value = Math.Max(_channel.MemoryManager.Read<uint>(address), payload);
                    _channel.MemoryManager.Write(address, value);
                }
                else
                {
                    _channel.MemoryManager.Write(address, payload);
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Dispatches a QMD 3.0 grid.
        /// </summary>
        private bool DispatchQmd(ReadOnlySpan<int> qmd)
        {
            ulong program = ((ulong)(uint)qmd[49] & 0x1ffff) << 32 | (uint)qmd[48];

            int sharedMemorySize = Math.Min(qmd[17] & 0x3ffff, _context.Capabilities.MaximumComputeSharedMemorySize);
            int localMemorySize = (qmd[23] & 0xffffff) + (qmd[50] & 0xffffff);

            int ctaX = Math.Max(qmd[18] >> 16, 1);
            int ctaY = Math.Max(qmd[19] & 0xffff, 1);
            int ctaZ = Math.Max(qmd[19] >> 16, 1);

            bool[] constantBufferValid = new bool[Constants.TotalCpUniformBuffers];
            ulong[] constantBufferAddress = new ulong[Constants.TotalCpUniformBuffers];
            ulong[] constantBufferSize = new ulong[Constants.TotalCpUniformBuffers];

            int enabled = qmd[20] & 0xff;

            for (int index = 0; index < Constants.TotalCpUniformBuffers; index++)
            {
                if ((enabled & (1 << index)) == 0)
                {
                    continue;
                }

                uint high = (uint)qmd[33 + index * 2];

                constantBufferValid[index] = true;
                constantBufferAddress[index] = (uint)qmd[32 + index * 2] | ((ulong)(high & 0xff) << 32);
                constantBufferSize[index] = ((ulong)((high >> 19) & 0x1fff)) << 4;
            }

            try
            {
                Dispatch(
                    program,
                    ctaX,
                    ctaY,
                    ctaZ,
                    localMemorySize,
                    sharedMemorySize,
                    SamplerIndex.Independently,
                    constantBufferValid,
                    constantBufferAddress,
                    constantBufferSize);
            }
            catch (Exception exception)
            {
                // The QMD still completes: its releases are what other queues wait on.
                Logger.Warning?.Print(LogClass.Gpu, $"QMD compute program {program:x16} rejected: {exception.Message}");

                return false;
            }

            return true;
        }

        /// <summary>
        /// Binds compute state and dispatches a grid.
        /// </summary>
        private void Dispatch(
            ulong shaderGpuVa,
            int ctaX,
            int ctaY,
            int ctaZ,
            int localMemorySize,
            int sharedMemorySize,
            SamplerIndex samplerIndex,
            bool[] constantBufferValid,
            ulong[] constantBufferAddress,
            ulong[] constantBufferSize)
        {
            ulong samplerPoolGpuVa = ((ulong)_state.State.SetTexSamplerPoolAOffsetUpper << 32) | _state.State.SetTexSamplerPoolB;
            ulong texturePoolGpuVa = ((ulong)_state.State.SetTexHeaderPoolAOffsetUpper << 32) | _state.State.SetTexHeaderPoolB;

            int samplerPoolMaximumId = _state.State.SetTexSamplerPoolCMaximumIndex;

            GpuChannelPoolState poolState = new(
                texturePoolGpuVa,
                _state.State.SetTexHeaderPoolCMaximumIndex,
                _state.State.SetBindlessTextureConstantBufferSlotSelect);

            for (int index = 0; index < Constants.TotalCpUniformBuffers; index++)
            {
                if (!constantBufferValid[index])
                {
                    continue;
                }

                _channel.BufferManager.SetComputeUniformBuffer(index, constantBufferAddress[index], constantBufferSize[index]);
            }

            GpuChannelComputeState computeState = new(
                ctaX,
                ctaY,
                ctaZ,
                localMemorySize,
                sharedMemorySize,
                _channel.BufferManager.HasUnalignedStorageBuffers);

            CachedShaderProgram cs = _channel.MemoryManager.Physical.ShaderCache.GetComputeShader(
                _channel,
                samplerPoolMaximumId,
                poolState,
                computeState,
                shaderGpuVa);

            _context.Renderer.Pipeline.SetProgram(cs.HostProgram);

            _channel.TextureManager.SetComputeSamplerPool(samplerPoolGpuVa, _state.State.SetTexSamplerPoolCMaximumIndex, samplerIndex);
            _channel.TextureManager.SetComputeTexturePool(texturePoolGpuVa, _state.State.SetTexHeaderPoolCMaximumIndex);
            _channel.TextureManager.SetComputeTextureBufferIndex(_state.State.SetBindlessTextureConstantBufferSlotSelect);

            ShaderProgramInfo info = cs.Shaders[0].Info;

            for (int index = 0; index < info.SBuffers.Count; index++)
            {
                BufferDescriptor sb = info.SBuffers[index];

                ulong sbDescAddress = _channel.BufferManager.GetComputeUniformBufferAddress(sb.SbCbSlot);
                sbDescAddress += (ulong)sb.SbCbOffset * 4;

                SbDescriptor sbDescriptor = _channel.MemoryManager.Physical.Read<SbDescriptor>(sbDescAddress);

                uint size;
                if (sb.SbCbSlot == Constants.DriverReservedUniformBuffer)
                {
                    // Only trust the SbDescriptor size if it comes from slot 0.
                    size = (uint)sbDescriptor.Size;
                }
                else
                {
                    // TODO: Use full mapped size and somehow speed up buffer sync.
                    size = (uint)_channel.MemoryManager.GetMappedSize(sbDescriptor.PackAddress(), Constants.MaxUnknownStorageSize);
                }

                _channel.BufferManager.SetComputeStorageBuffer(sb.Slot, sbDescriptor.PackAddress(), size, sb.Flags);
            }

            if (_channel.BufferManager.HasUnalignedStorageBuffers != computeState.HasUnalignedStorageBuffer)
            {
                // Refetch the shader, as assumptions about storage buffer alignment have changed.
                computeState = new GpuChannelComputeState(
                    ctaX,
                    ctaY,
                    ctaZ,
                    localMemorySize,
                    sharedMemorySize,
                    _channel.BufferManager.HasUnalignedStorageBuffers);

                cs = _channel.MemoryManager.Physical.ShaderCache.GetComputeShader(
                    _channel,
                    samplerPoolMaximumId,
                    poolState,
                    computeState,
                    shaderGpuVa);

                _context.Renderer.Pipeline.SetProgram(cs.HostProgram);
            }

            _channel.BufferManager.SetComputeBufferBindings(cs.Bindings);

            _channel.TextureManager.SetComputeBindings(cs.Bindings);

            // Should never return false for mismatching spec state, since the shader was fetched above.
            _channel.TextureManager.CommitComputeBindings(cs.SpecializationState);

            _channel.BufferManager.CommitComputeBindings();

            _context.Renderer.Pipeline.DispatchCompute(ctaX, ctaY, ctaZ);

            _3dEngine.ForceShaderUpdate();
        }
    }
}
