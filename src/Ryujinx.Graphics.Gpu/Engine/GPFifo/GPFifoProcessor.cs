using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using Ryujinx.Graphics.Gpu.Engine.Dma;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Twod;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO command processor.
    /// </summary>
    /// <summary>
    /// Public entry point for the HLE side to resume channels suspended on a semaphore acquire.
    /// </summary>
    public static class Nx2GpuStall
    {
        public static void TryResumeAll()
        {
            GPFifoProcessor.TryResumeAll();
        }

        public static int MacrosRun => Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.MacrosRun;
        public static int MacroMemoHits => Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.MacroMemoHits;
        public static int MacroMemoMisses => Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.MacroMemoMisses;
        public static int EmittedMethods => Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.EmittedMethods;
        public static int Draws => Ryujinx.Graphics.Gpu.Engine.Threed.ThreedClass.DrawCount;

        public static long UnboundDrops => GPFifoProcessor.UnboundDrops;

        public static string Tolerances => $"U={Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.TolUninitReg} F={Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.TolFifoUnderflow} S={Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.TolBadSelector} O={Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.TolOobEnd}";

        public static string Fifo => $"bufs={GPFifoProcessor.BuffersProcessed} words={GPFifoProcessor.WordsProcessed} spwait={GPFifoClass.SyncpointWaits} spincr={GPFifoClass.SyncpointIncrs} eng=[{string.Join(",", GPFifoProcessor.EngineCounts)}]";

        public static string ComputeMethods()
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>> list;

            lock (GPFifoProcessor.ComputeHistogram)
            {
                list = [.. GPFifoProcessor.ComputeHistogram];
            }

            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            System.Text.StringBuilder sb = new();

            for (int i = 0; i < list.Count && i < 16; i++)
            {
                sb.Append($"0x{list[i].Key:X}={list[i].Value} ");
            }

            return sb.ToString();
        }

        /// <summary>Full distribution of macro-emitted method addresses.</summary>
        public static string DescribeEmitted()
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, int>> list = new(Ryujinx.Graphics.Gpu.Engine.MME.MacroTuring.EmittedHistogram);

            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            System.Text.StringBuilder sb = new();

            foreach (System.Collections.Generic.KeyValuePair<uint, int> kv in list)
            {
                sb.Append($"0x{kv.Key:X}={kv.Value} ");
            }

            return sb.ToString();
        }

        /// <summary>Total 3D methods dispatched and the most frequent method offsets.</summary>
        public static string Describe3DMethods()
        {
            System.Text.StringBuilder sb = new();
            sb.Append($"total={GPFifoProcessor.Method3DTotal} top=");

            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>> list;

            lock (GPFifoProcessor.MethodHistogram)
            {
                list = [.. GPFifoProcessor.MethodHistogram];
            }

            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            for (int i = 0; i < list.Count && i < 14; i++)
            {
                sb.Append($"0x{list[i].Key:X}={list[i].Value} ");
            }

            return sb.ToString();
        }
    }

    class GPFifoProcessor : IDisposable
    {
        private const int MacrosCount = 0x80;
        private const int MacroIndexMask = MacrosCount - 1;

        private const int LoadInlineDataMethodOffset = 0x6d;
        private const int UniformBufferUpdateDataMethodOffset = 0x8e4;

        // The GPFIFO has 8 subchannels; Switch 2 titles use the last ones too, and a bound
        // subchannel outside this range would drop every method sent to it.
        private const int SubChannelCount = 8;

        private readonly GpuContext _context;
        private readonly GpuChannel _channel;

        // Engine each subchannel is bound to, for Switch 2 streams. Ampere binds object classes
        // rather than the Maxwell engine IDs; values are indices into the dispatch switch below.
        private const int Engine3D = 0;
        private const int EngineCompute = 1;
        private const int EngineInlineToMemory = 2;
        private const int Engine2D = 3;
        private const int EngineDma = 4;
        private const int EngineFifo = 5;

        private readonly int[] _subChannelEngine = new int[SubChannelCount];

        internal static int Method3DTotal => _method3DTotal;
        internal static readonly System.Collections.Generic.Dictionary<int, int> MethodHistogram = [];
        internal static long WordsProcessed;
        internal static long BuffersProcessed;
        internal static readonly long[] EngineCounts = new long[6];
        internal static readonly System.Collections.Generic.Dictionary<int, int> ComputeHistogram = [];

        /// <summary>
        /// Methods dropped because their subchannel is unbound (class 0) or bound to
        /// an engine with no MME/device-state handler. Named and counted instead of
        /// silently skipped: a rising count with no draws means the guest drives an
        /// engine this build does not serve.
        /// </summary>
        internal static long UnboundDrops;

        private static int _unboundDropLogs;

        private static int _method3DTotal;

        private sealed class StallException : System.Exception;

        private bool _stalled;
        private ulong _stallAddress;
        private uint _stallValue;
        private bool _stallExact;
        private ulong _stallBaseVa;
        private int _stallWordCount;
        private int _stallIndex;
        private long _stallSince;

        private static readonly System.Collections.Generic.List<GPFifoProcessor> Stalled = [];
        private static bool _resuming;

        // Object classes the nx2 binder has not seen before, logged once each.
        private readonly System.Collections.Generic.HashSet<int> _unknownClasses = [];

        // Gated probe (AGENTS.md section 9): records the object classes the Switch 2
        // binder sees, one capped file per run.
        private static readonly bool GpuClassLogEnabled = Ryujinx.Common.Nx2ProbeLog.Enabled("NX2_GPU_CLASS_LOG");

        /// <summary>
        /// Suspends this channel on an unmet semaphore acquire. Returns false when the channel is
        /// already stalled (the caller then resets or ignores the acquire instead of nesting).
        /// </summary>
        public bool StallOnAcquire(ulong address, int value, bool exact)
        {
            if (_stalled)
            {
                return false;
            }

            _stalled = true;
            _stallAddress = address;
            _stallValue = (uint)value;
            _stallExact = exact;
            _stallBaseVa = _bufferBaseVa;
            _stallWordCount = _bufferWords;
            _stallIndex = _bufferIndex;
            _stallSince = System.Environment.TickCount64;

            lock (Stalled)
            {
                if (!Stalled.Contains(this))
                {
                    Stalled.Add(this);
                }
            }

            throw new StallException();
        }

        private bool AcquireMet()
        {
            uint current = (uint)MemoryManager.Read<int>(_stallAddress);

            return _stallExact ? current == _stallValue : current >= _stallValue;
        }

        /// <summary>
        /// Resumes channels whose acquire was met (or that waited too long).
        /// </summary>
        public static void TryResumeAll()
        {
            GPFifoProcessor[] stalled;

            lock (Stalled)
            {
                if (Stalled.Count == 0 || _resuming)
                {
                    return;
                }

                _resuming = true;
                stalled = [.. Stalled];
            }

            try
            {
                foreach (GPFifoProcessor processor in stalled)
                {
                    processor.TryResume();
                }
            }
            finally
            {
                lock (Stalled)
                {
                    _resuming = false;
                }
            }
        }

        private static int _stallTimeouts;

        private void TryResume()
        {
            if (!_stalled)
            {
                lock (Stalled)
                {
                    Stalled.Remove(this);
                }

                return;
            }

            bool met = AcquireMet();
            bool timedOut = !met && System.Environment.TickCount64 - _stallSince >= 500;

            if (!met && !timedOut)
            {
                return;
            }

            if (timedOut && System.Threading.Interlocked.Increment(ref _stallTimeouts) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"nx2: semaphore acquire 0x{_stallAddress:X} not met in 500 ms (wants {_stallValue}, has {(uint)MemoryManager.Read<int>(_stallAddress)}); continuing");
            }

            _stalled = false;

            lock (Stalled)
            {
                Stalled.Remove(this);
            }

            int count = _stallWordCount;
            int[] words = new int[count];

            for (int i = 0; i < count; i++)
            {
                words[i] = MemoryManager.Read<int>(_stallBaseVa + (ulong)i * 4);
            }

            ProcessLoop(_stallBaseVa, words, _stallIndex);
        }

        /// <summary>
        /// Channel memory manager.
        /// </summary>
        public MemoryManager MemoryManager => _channel.MemoryManager;

        /// <summary>
        /// Channel texture manager.
        /// </summary>
        public TextureManager TextureManager => _channel.TextureManager;

        /// <summary>
        /// Per-channel MME data RAM, used by the Switch 2 (Turing+) macro engine.
        /// </summary>
        public int[] MmeDataRam => _fifoClass.MmeDataRam;

        /// <summary>
        /// Counter that changes when macro code is uploaded on this channel.
        /// </summary>
        public long MmeCodeGeneration => _fifoClass.MmeCodeGeneration;

        /// <summary>
        /// Counter that changes when compute macro code is uploaded on this channel.
        /// </summary>
        public long ComputeMmeCodeGeneration => _fifoClass.ComputeMmeCodeGeneration;

        /// <summary>
        /// 3D Engine.
        /// </summary>
        public ThreedClass ThreedClass => _3dClass;

        /// <summary>
        /// Internal GPFIFO state.
        /// </summary>
        private struct DmaState
        {
            public int Method;
            public int SubChannel;
            public int MethodCount;
            public bool NonIncrementing;
            public bool IncrementOnce;
        }

        private DmaState _state;

        private readonly ThreedClass _3dClass;
        private readonly ComputeClass _computeClass;
        private readonly InlineToMemoryClass _i2mClass;
        private readonly TwodClass _2dClass;
        private readonly DmaClass _dmaClass;

        private readonly GPFifoClass _fifoClass;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO command processor.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">Channel that the GPFIFO processor belongs to</param>
        public GPFifoProcessor(GpuContext context, GpuChannel channel)
        {
            _context = context;
            _channel = channel;

            for (int index = 0; index < _subChannelEngine.Length; index++)
            {
                _subChannelEngine[index] = index;
            }

            _fifoClass = new GPFifoClass(context, this);
            _3dClass = new ThreedClass(context, channel, _fifoClass);
            _computeClass = new ComputeClass(context, channel, _3dClass, _fifoClass);
            _i2mClass = new InlineToMemoryClass(context, channel);
            _2dClass = new TwodClass(channel);
            _dmaClass = new DmaClass(context, channel, _3dClass);
        }

        /// <summary>
        /// Processes a command buffer.
        /// </summary>
        /// <param name="baseGpuVa">Base GPU virtual address of the command buffer</param>
        /// <param name="commandBuffer">Command buffer</param>
        public void Process(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer)
        {
            _bufferBaseVa = baseGpuVa;
            _bufferWords = commandBuffer.Length;

            System.Threading.Interlocked.Add(ref WordsProcessed, commandBuffer.Length);
            System.Threading.Interlocked.Increment(ref BuffersProcessed);

            try
            {
                ProcessLoop(baseGpuVa, commandBuffer, 0);
            }
            catch (StallException)
            {
                // Resume point recorded by StallOnAcquire.
            }

            _3dClass.FlushUboDirty();
        }

        private ulong _bufferBaseVa;
        private int _bufferWords;
        private int _bufferIndex;

        private void ProcessLoop(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer, int startIndex)
        {
            for (int index = startIndex; index < commandBuffer.Length; index++)
            {
                _bufferIndex = index;
                int command = commandBuffer[index];

                ulong gpuVa = baseGpuVa + (ulong)index * 4;

                if (_state.MethodCount != 0)
                {
                    if (TryFastI2mBufferUpdate(commandBuffer, ref index))
                    {
                        continue;
                    }

                    Send(gpuVa, _state.Method, command, _state.SubChannel, _state.MethodCount <= 1);

                    if (!_state.NonIncrementing)
                    {
                        _state.Method++;
                    }

                    if (_state.IncrementOnce)
                    {
                        _state.NonIncrementing = true;
                    }

                    _state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    if (TryFastUniformBufferUpdate(meth, commandBuffer, index))
                    {
                        index += meth.MethodCount;
                        continue;
                    }

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            _state.Method = meth.MethodAddress;
                            _state.SubChannel = meth.MethodSubchannel;
                            _state.MethodCount = meth.MethodCount;
                            _state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            _state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            Send(gpuVa, meth.MethodAddress, meth.ImmdData, meth.MethodSubchannel, true);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Tries to perform a fast Inline-to-Memory data update.
        /// If successful, all data will be copied at once, and <see cref="DmaState.MethodCount"/>
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="commandBuffer">Command buffer where the data is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where the data is located, auto-incremented on success</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastI2mBufferUpdate(ReadOnlySpan<int> commandBuffer, ref int offset)
        {
            if (_state.Method == LoadInlineDataMethodOffset && _state.NonIncrementing && _state.SubChannel <= 2)
            {
                int availableCount = commandBuffer.Length - offset;
                int consumeCount = Math.Min(_state.MethodCount, availableCount);

                ReadOnlySpan<int> data = commandBuffer.Slice(offset, consumeCount);

                if (_state.SubChannel == 0)
                {
                    _3dClass.LoadInlineData(data);
                }
                else if (_state.SubChannel == 1)
                {
                    _computeClass.LoadInlineData(data);
                }
                else /* if (_state.SubChannel == 2) */
                {
                    _i2mClass.LoadInlineData(data);
                }

                offset += consumeCount - 1;
                _state.MethodCount -= consumeCount;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to perform a fast constant buffer data update.
        /// If successful, all data will be copied at once, and <see cref="CompressedMethod.MethodCount"/> + 1
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="meth">Compressed method to be checked</param>
        /// <param name="commandBuffer">Command buffer where <paramref name="meth"/> is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where <paramref name="meth"/> is located</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastUniformBufferUpdate(CompressedMethod meth, ReadOnlySpan<int> commandBuffer, int offset)
        {
            int availableCount = commandBuffer.Length - offset;

            if (meth.MethodAddress == UniformBufferUpdateDataMethodOffset &&
                meth.MethodCount < availableCount &&
                meth.SecOp == SecOp.NonIncMethod)
            {
                _3dClass.ConstantBufferUpdate(commandBuffer.Slice(offset + 1, meth.MethodCount));

                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends a uncompressed method for processing by the graphics pipeline.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="meth">Method to be processed</param>
        private void Send(ulong gpuVa, int offset, int argument, int subChannel, bool isLastCall)
        {
            if (offset == 0)
            {
                BindObject(subChannel, argument);
            }

            if (offset < 0x60)
            {
                _fifoClass.Write(offset * 4, argument);
            }
            else if (offset < 0xe00)
            {
                offset *= 4;

                switch (SubChannelEngine(subChannel))
                {
                    case Engine3D:
                        _3dClass.Write(offset, argument);
                        break;
                    case EngineCompute:
                        _computeClass.Write(offset, argument);
                        break;
                    case EngineInlineToMemory:
                        _i2mClass.Write(offset, argument);
                        break;
                    case Engine2D:
                        _2dClass.Write(offset, argument);
                        break;
                    case EngineDma:
                        _dmaClass.Write(offset, argument);
                        break;
                    case EngineFifo:
                        _fifoClass.Write(offset, argument);
                        break;
                    default:
                        LogUnboundDrop(gpuVa, offset, argument, subChannel);
                        break;
                }
            }
            else
            {
                int engine = SubChannelEngine(subChannel);

                // An unbound subchannel yields an engine id outside the counted set.
                if ((uint)engine < (uint)EngineCounts.Length)
                {
                    System.Threading.Interlocked.Increment(ref EngineCounts[engine]);
                }

                if (engine == EngineCompute)
                {
                    lock (ComputeHistogram)
                    {
                        ComputeHistogram.TryGetValue(offset, out int seen);
                        ComputeHistogram[offset] = seen + 1;
                    }
                }

                if (engine == Engine3D)
                {
                    System.Threading.Interlocked.Increment(ref _method3DTotal);
                    lock (MethodHistogram)
                    {
                        MethodHistogram.TryGetValue(offset, out int seen);
                        MethodHistogram[offset] = seen + 1;
                    }
                }

                IDeviceState state = engine switch
                {
                    Engine3D => _3dClass,
                    EngineCompute => _context.IsNx2Ampere ? _computeClass : null,
                    Engine2D => _2dClass,
                    _ => null,
                };

                if (state == null)
                {
                    LogUnboundDrop(gpuVa, offset, argument, subChannel);

                    return;
                }

                int macroIndex = (offset >> 1) & MacroIndexMask;
                bool compute = engine == EngineCompute;

                if ((offset & 1) != 0)
                {
                    if (compute)
                    {
                        _fifoClass.ComputeMmePushArgument(macroIndex, gpuVa, argument);
                    }
                    else
                    {
                        _fifoClass.MmePushArgument(macroIndex, gpuVa, argument);
                    }
                }
                else
                {
                    if (compute)
                    {
                        _fifoClass.ComputeMmeStart(macroIndex, argument);
                    }
                    else
                    {
                        _fifoClass.MmeStart(macroIndex, argument);
                    }
                }

                if (isLastCall)
                {
                    if (compute)
                    {
                        _fifoClass.ComputeCallMme(macroIndex, state);
                    }
                    else
                    {
                        _fifoClass.CallMme(macroIndex, state);
                    }

                    _3dClass.PerformDeferredDraws();
                }
            }
        }

        /// <summary>
        /// Records a method dropped because its subchannel is unbound or bound to an
        /// engine with no MME/device-state handler. Capped logging; the total is read
        /// from the progress line next to the draw count.
        /// </summary>
        private static void LogUnboundDrop(ulong gpuVa, int offset, int argument, int subChannel)
        {
            System.Threading.Interlocked.Increment(ref UnboundDrops);

            if (System.Threading.Interlocked.Increment(ref _unboundDropLogs) <= 8)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"NX2 unbound subchannel={subChannel} offset=0x{offset:X} value=0x{argument:X8} gpuVa=0x{gpuVa:X}");
            }
        }

        /// <summary>
        /// Binds a subchannel to the engine selected by an object class.
        /// </summary>
        /// <param name="subChannel">Subchannel being bound</param>
        /// <param name="argument">Class ID (and engine ID in the upper bits)</param>
        private void BindObject(int subChannel, int argument)
        {
            if ((uint)subChannel >= SubChannelCount)
            {
                return;
            }

            // Single class-to-engine map for both GPU generations (Switch-2-only build:
            // no Maxwell fallback path). Ampere object classes share the Maxwell method
            // layout, so the existing engines process them; only the mapping differs.
            int classId = argument & 0xffff;

            if (classId == 0)
            {
                // Class 0 unbinds the subchannel. -1 matches no engine, so its calls are dropped.
                _subChannelEngine[subChannel] = -1;
                return;
            }

            int engineField = (argument >> 16) & 0x1f;

            _subChannelEngine[subChannel] = classId switch
            {
                0xc797 => Engine3D,        // AMPERE_B
                0xc7c0 => EngineCompute,   // AMPERE_COMPUTE_B
                0xc7b5 => EngineDma,       // AMPERE_DMA_COPY_B
                0xc56f => EngineFifo,      // AMPERE_CHANNEL_GPFIFO_A
                _ => subChannel,           // unknown class: keep the legacy mapping
            };

            if (classId is not (0xc797 or 0xc7c0 or 0xc7b5 or 0xc56f) && _unknownClasses.Add(classId))
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceNv,
                    $"Switch 2 unknown object class 0x{classId:X4} (engine {engineField}) on subchannel {subChannel}");
            }

            if (GpuClassLogEnabled)
            {
                bool known = classId == 0 || classId is 0xc797 or 0xc7c0 or 0xc7b5 or 0xc56f;

                Ryujinx.Common.Nx2ProbeLog.Append("nx2-gpu-class.log",
                    $"sub={subChannel} class=0x{classId:X4} engineField={engineField} known={known} engine={_subChannelEngine[subChannel]}");
            }
        }

        /// <summary>
        /// Gets the engine index a subchannel currently dispatches to, or -1 when unbound.
        /// </summary>
        private int SubChannelEngine(int subChannel)
        {
            if ((uint)subChannel >= SubChannelCount)
            {
                return subChannel;
            }

            int engine = _subChannelEngine[subChannel];

            if (engine == -1)
            {
                return EngineFifo + 1; // No matching case: drop the call.
            }

            return engine;
        }

        /// <summary>
        /// Writes data directly to the state of the specified class.
        /// </summary>
        /// <param name="classId">ID of the class to write the data into</param>
        /// <param name="offset">State offset in bytes</param>
        /// <param name="value">Value to be written</param>
        public void Write(ClassId classId, int offset, int value)
        {
            switch (classId)
            {
                case ClassId.Threed:
                    _3dClass.Write(offset, value);
                    break;
                case ClassId.Compute:
                    _computeClass.Write(offset, value);
                    break;
                case ClassId.InlineToMemory:
                    _i2mClass.Write(offset, value);
                    break;
                case ClassId.Twod:
                    _2dClass.Write(offset, value);
                    break;
                case ClassId.Dma:
                    _dmaClass.Write(offset, value);
                    break;
                case ClassId.GPFifo:
                    _fifoClass.Write(offset, value);
                    break;
            }
        }

        /// <summary>
        /// Sets the shadow ram control value of all sub-channels.
        /// </summary>
        /// <param name="control">New shadow ram control value</param>
        public void SetShadowRamControl(int control)
        {
            _3dClass.SetShadowRamControl(control);
        }

        /// <summary>
        /// Forces a full host state update by marking all state as modified,
        /// and also requests all GPU resources in use to be rebound.
        /// </summary>
        public void ForceAllDirty()
        {
            _3dClass.ForceStateDirty();
            _channel.BufferManager.Rebind();
            _channel.TextureManager.Rebind();
        }

        /// <summary>
        /// Perform any deferred draws.
        /// </summary>
        public void PerformDeferredDraws()
        {
            _3dClass.PerformDeferredDraws();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _3dClass.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
