using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.MME;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Synchronization;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO class.
    /// </summary>
    class GPFifoClass : IDeviceState
    {
        private readonly GpuContext _context;
        private readonly GPFifoProcessor _parent;
        private readonly DeviceState<GPFifoClassState> _state;

        private bool _createSyncPending;

        private const int MacrosCount = 0x80;

        // Note: The size of the macro memory is unknown, we just make
        // a guess here and use 256kb as the size. Increase if needed.
        private const int MacroCodeSize = 256 * 256;

        private readonly Macro[] _macros;
        private readonly int[] _macroCode;

        // Switch 2 compute streams upload their own macro code and start addresses through
        // compute methods 0x45-0x48, separately from the 3D/fifo MME.
        private readonly Macro[] _computeMacros = new Macro[MacrosCount];
        private readonly int[] _computeMacroCode = new int[MacroCodeSize];
        private int _computeCodeWriteOffset;
        private int _computeStartPointer;
        private long _computeCodeGeneration;

        // Switch 2 (Turing+) MME keeps a per-channel data RAM, addressed by the macro DREAD/DWRITE
        // operations. Each channel owns one; a channel's 3D and compute macros share it.
        private readonly int[] _mmeDataRam = new int[0xc00];
        private int _mmeCodeWriteOffset;
        private long _mmeCodeGeneration;

        /// <summary>
        /// Per-channel MME data RAM.
        /// </summary>
        public int[] MmeDataRam => _mmeDataRam;

        /// <summary>
        /// Counter that changes whenever macro code is uploaded, for memo invalidation.
        /// </summary>
        public long MmeCodeGeneration => _mmeCodeGeneration;

        /// <summary>
        /// Counter that changes when compute macro code is uploaded, for memo invalidation.
        /// </summary>
        public long ComputeMmeCodeGeneration => _computeCodeGeneration;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO class.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="parent">Parent GPU General Purpose FIFO processor</param>
        public GPFifoClass(GpuContext context, GPFifoProcessor parent)
        {
            _context = context;
            _parent = parent;
            _state = new DeviceState<GPFifoClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(GPFifoClassState.Semaphored), new RwCallback(Semaphored, null) },
                { nameof(GPFifoClassState.Syncpointb), new RwCallback(Syncpointb, null) },
                { nameof(GPFifoClassState.WaitForIdle), new RwCallback(WaitForIdle, null) },
                { nameof(GPFifoClassState.SetReference), new RwCallback(SetReference, null) },
                { nameof(GPFifoClassState.LoadMmeInstructionRam), new RwCallback(LoadMmeInstructionRam, null) },
                { nameof(GPFifoClassState.LoadMmeInstructionRamPointer), new RwCallback(LoadMmeInstructionRamPointer, null) },
                { nameof(GPFifoClassState.LoadMmeStartAddressRam), new RwCallback(LoadMmeStartAddressRam, null) },
                { nameof(GPFifoClassState.SetMmeShadowRamControl), new RwCallback(SetMmeShadowRamControl, null) },
            });

            _macros = new Macro[MacrosCount];
            _macroCode = new int[MacroCodeSize];
        }

        /// <summary>
        /// Create any syncs from WaitForIdle command that are currently pending.
        /// </summary>
        public void CreatePendingSyncs()
        {
            if (_createSyncPending)
            {
                _createSyncPending = false;
                _context.CreateHostSyncIfNeeded(HostSyncFlags.None);
            }
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
        public void Write(int offset, int data) => _state.Write(offset, data);

        private static readonly System.Collections.Generic.HashSet<ulong> _unbackedSemaphorePages = [];

        /// <summary>
        /// Returns false when nothing backs <paramref name="address"/> in the guest address space.
        /// </summary>
        private bool IsSemaphoreAddressBacked(ulong address)
        {
            return _parent.MemoryManager.IsGpuAddressBacked(address);
        }

        private static readonly System.Collections.Generic.HashSet<ulong> _adoptedSemaphorePages = [];

        /// <summary>
        /// Backs the page holding <paramref name="address"/> with host memory so a semaphore
        /// release targeted at a GPU address the guest never mapped still lands somewhere the
        /// guest's waits can observe.
        /// </summary>
        /// <param name="address">GPU address of the release</param>
        /// <returns>True if the page is backed afterwards</returns>
        private static bool AdoptSemaphorePage(ulong address)
        {
            ulong page = address & ~(MemoryManager.PageSize - 1);

            lock (_adoptedSemaphorePages)
            {
                if (_adoptedSemaphorePages.Contains(page))
                {
                    return true;
                }
            }

            if (!Nx2GpuMemory.CreateRegion(page, MemoryManager.PageSize, eager: false))
            {
                return false;
            }

            lock (_adoptedSemaphorePages)
            {
                _adoptedSemaphorePages.Add(page);

                if (_adoptedSemaphorePages.Count <= 8)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceNv,
                        $"Switch 2 semaphore release to unbacked GPU address 0x{address:X}: page given host backing");
                }
            }

            return true;
        }

        private static void LogUnbackedSemaphore(ulong address)
        {
            ulong page = address & ~0xFFFUL;

            lock (_unbackedSemaphorePages)
            {
                if (_unbackedSemaphorePages.Count >= 16 || !_unbackedSemaphorePages.Add(page))
                {
                    return;
                }
            }

            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.ServiceNv,
                $"Switch 2 semaphore release to unbacked GPU address 0x{address:X} dropped");
        }

        /// <summary>
        /// Writes a GPU counter to guest memory.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void Semaphored(int argument)
        {
            ulong address = ((ulong)_state.State.SemaphorebOffsetLower << 2) |
                            ((ulong)_state.State.SemaphoreaOffsetUpper << 32);

            int value = _state.State.SemaphorecPayload;

            SemaphoredOperation operation = _state.State.SemaphoredOperation;

            // Switch 2 pushes releases into a GPU address space that is not the guest one, so a
            // queue's semaphore page can land on an address the guest never mapped. The console
            // writes into that page; here it has no backing, so the release is dropped.
            if (!IsSemaphoreAddressBacked(address))
            {
                // The guest waits on these release values, so give the page host backing instead
                // of dropping the write: reads then see the same value the console would.
                if (!AdoptSemaphorePage(address))
                {
                    LogUnbackedSemaphore(address);

                    return;
                }
            }

            if (_state.State.SemaphoredReleaseSize == SemaphoredReleaseSize.SixteenBytes)
            {
                _parent.MemoryManager.Write(address + 4, 0);
                _parent.MemoryManager.Write(address + 8, _context.GetTimestamp());
            }

            // A semaphore acquire gates the rest of the command buffer: the CPU (or another
            // channel) releases the value once it has written the commands this channel runs
            // next. Running past an unmet acquire executes stale commands and leaves the guest
            // waiting for work that never completes.
            if (operation is SemaphoredOperation.Acquire or SemaphoredOperation.AcqGeq)
            {
                int current = _parent.MemoryManager.Read<int>(address);

                bool met = operation == SemaphoredOperation.Acquire
                    ? current == value
                    : (uint)current >= (uint)value;

                if (!met && _parent.StallOnAcquire(address, value, operation == SemaphoredOperation.Acquire))
                {
                    return;
                }
            }

            // TODO: interrupts for invalid combinations.
            if (operation == SemaphoredOperation.Release)
            {
                _parent.MemoryManager.Write(address, value);

                if (Nx2GpuMemory.TryGetSemaphoreMirror(address, out ulong mirror))
                {
                    _parent.MemoryManager.Write(mirror, value);
                }
            }
            else if (operation == SemaphoredOperation.Reduction)
            {
                bool signed = _state.State.SemaphoredFormat == SemaphoredFormat.Signed;

                int mem = _parent.MemoryManager.Read<int>(address);

                switch (_state.State.SemaphoredReduction)
                {
                    case SemaphoredReduction.Min:
                        value = signed ? Math.Min(mem, value) : (int)Math.Min((uint)mem, (uint)value);
                        break;
                    case SemaphoredReduction.Max:
                        value = signed ? Math.Max(mem, value) : (int)Math.Max((uint)mem, (uint)value);
                        break;
                    case SemaphoredReduction.Xor:
                        value ^= mem;
                        break;
                    case SemaphoredReduction.And:
                        value &= mem;
                        break;
                    case SemaphoredReduction.Or:
                        value |= mem;
                        break;
                    case SemaphoredReduction.Add:
                        value += mem;
                        break;
                    case SemaphoredReduction.Inc:
                        value = (uint)mem < (uint)value ? mem + 1 : 0;
                        break;
                    case SemaphoredReduction.Dec:
                        value = (uint)mem > 0 && (uint)mem <= (uint)value ? mem - 1 : value;
                        break;
                }

                _parent.MemoryManager.Write(address, value);
            }
        }

        /// <summary>
        /// Apply a fence operation on a syncpoint.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void Syncpointb(int argument)
        {
            SyncpointbOperation operation = _state.State.SyncpointbOperation;

            uint syncpointId = (uint)_state.State.SyncpointbSyncptIndex;

            if (operation == SyncpointbOperation.Wait)
            {
                uint threshold = (uint)_state.State.SyncpointaPayload;

                System.Threading.Interlocked.Increment(ref SyncpointWaits);

                _context.Synchronization.WaitOnSyncpoint(syncpointId, threshold, Timeout.InfiniteTimeSpan);
            }
            else if (operation == SyncpointbOperation.Incr)
            {
                System.Threading.Interlocked.Increment(ref SyncpointIncrs);

                // "Unbind" render targets since a syncpoint increment might indicate future CPU access for the textures.
                _parent.TextureManager.RefreshModifiedTextures();

                _context.CreateHostSyncIfNeeded(HostSyncFlags.StrictSyncpoint);
                uint after = _context.Synchronization.IncrementSyncpoint(syncpointId);

                if (System.Environment.GetEnvironmentVariable("NX2_SUBMIT_LOG") == "1" &&
                    System.Threading.Interlocked.Increment(ref SyncpointIncrLogs) <= 10)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"NX2 spincr id={syncpointId} now={after} check={_context.Synchronization.GetSyncpointValue(syncpointId)}");
                    Ryujinx.Common.Nx2ProbeLog.Append("nx2-submit.log",
                        $"spincr id={syncpointId} now={after} check={_context.Synchronization.GetSyncpointValue(syncpointId)}");
                }
            }

            _context.AdvanceSequence();
        }

        /// <summary>
        /// Waits for the GPU to be idle.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void WaitForIdle(int argument)
        {
            _parent.PerformDeferredDraws();
            _context.Renderer.Pipeline.Barrier();

            _createSyncPending = true;
        }

        /// <summary>
        /// Used as an indirect data barrier on NVN. When used, access to previously written data must be coherent.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void SetReference(int argument)
        {
            _context.Renderer.Pipeline.CommandBufferBarrier();

            _context.CreateHostSyncIfNeeded(HostSyncFlags.Strict);
        }

        /// <summary>
        /// Sends macro code/data to the MME.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void LoadMmeInstructionRam(int argument)
        {
            // Switch-2-only build: Turing+ instructions are 96 bits (three words).
            // The driver names the start method in LoadMmeInstructionRamPointer and
            // streams words after it; code for method M lives at word offset M * 3,
            // matching Mesa's mme_tu104 layout.
            if ((uint)_mmeCodeWriteOffset < (uint)_macroCode.Length)
            {
                _macroCode[_mmeCodeWriteOffset++] = argument;
                _mmeCodeGeneration++;
            }

            return;
        }

        /// <summary>
        /// Sets the MME instruction write pointer.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void LoadMmeInstructionRamPointer(int argument)
        {
            // Switch-2-only build: the pointer register names an instruction index;
            // the code array is word packed. Clamp hostile indexes so the following
            // writes stay inside the macro RAM.
            _mmeCodeWriteOffset = Math.Clamp(argument, 0, _macroCode.Length / 3) * 3;
            _mmeCodeGeneration++;
        }

        /// <summary>
        /// Binds a macro index to a position for the MME
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void LoadMmeStartAddressRam(int argument)
        {
            int pointer = (int)_state.State.LoadMmeStartAddressRamPointer;

            if ((uint)pointer < (uint)_macros.Length)
            {
                _macros[pointer] = new Macro(argument);
                _state.State.LoadMmeStartAddressRamPointer = (uint)(pointer + 1);
            }
        }

        /// <summary>
        /// Changes the shadow RAM control.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        public void SetMmeShadowRamControl(int argument)
        {
            _parent.SetShadowRamControl(argument);
        }

        /// <summary>
        /// Pushes an argument to a macro.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="argument">Argument to be pushed to the macro</param>
        public void MmePushArgument(int index, ulong gpuVa, int argument)
        {
            _macros[index].PushArgument(gpuVa, argument);
        }

        /// <summary>
        /// Prepares a macro for execution.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="argument">Initial argument passed to the macro</param>
        public void MmeStart(int index, int argument)
        {
            _macros[index].StartExecution(_context, _parent, _macroCode, argument);
        }

        /// <summary>
        /// Executes a macro.
        /// </summary>
        /// <param name="index">Index of the macro</param>
        /// <param name="state">Current GPU state</param>
        private static int _callProbe;

        internal static int SyncpointWaits;
        internal static int SyncpointIncrs;

        private static int SyncpointIncrLogs;

        public void CallMme(int index, IDeviceState state)
        {
            // Turing MME64 macros are the only path Switch 2 drivers use for 3D state
            // and draws: skipping them yields zero methods and a black screen (measured:
            // 890 methods emitted, 0 failures with them on). NX2_RUN_MME=0 keeps the
            // old skip as a kill-switch.
            if (_context.IsNx2Ampere && System.Environment.GetEnvironmentVariable("NX2_RUN_MME") == "0")
            {
                return;
            }

            if (System.Environment.GetEnvironmentVariable("NX2_MACRO_LOG") == "1")
            {
                int logged = System.Threading.Interlocked.Increment(ref _callProbe);

                if (logged <= 20)
                {
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"NX2 mme call idx={index} pos={_macros[index].Position} codeGen={_mmeCodeGeneration} writeOff={_mmeCodeWriteOffset}");
                }
            }

            _macros[index].Execute(_macroCode, state);
        }

        /// <summary>
        /// Clears (sets the write position of) the compute MME code.
        /// </summary>
        /// <param name="argument">Instruction index to write at</param>
        public void KeepComputeMmeCode(int argument)
        {
            _computeCodeWriteOffset = Math.Min(argument, _computeMacroCode.Length / 3) * 3;
            _computeCodeGeneration++;
        }

        /// <summary>
        /// Appends a word to the compute MME code.
        /// </summary>
        /// <param name="argument">Code word</param>
        public void AddComputeMmeCode(int argument)
        {
            if (_computeCodeWriteOffset < _computeMacroCode.Length)
            {
                _computeMacroCode[_computeCodeWriteOffset++] = argument;
                _computeCodeGeneration++;
            }
        }

        /// <summary>
        /// Sets the compute MME start address bind pointer.
        /// </summary>
        /// <param name="argument">Macro slot index</param>
        public void SetComputeMmeBindPointer(int argument)
        {
            _computeStartPointer = argument;
        }

        /// <summary>
        /// Binds a compute macro start address.
        /// </summary>
        /// <param name="argument">Instruction index where the macro starts</param>
        public void BindComputeMmeStart(int argument)
        {
            if ((uint)_computeStartPointer < MacrosCount)
            {
                _computeMacros[_computeStartPointer++] = new Macro(argument);
            }
        }

        /// <summary>
        /// Pushes an argument to a compute macro.
        /// </summary>
        public void ComputeMmePushArgument(int index, ulong gpuVa, int argument)
        {
            _computeMacros[index].PushArgument(gpuVa, argument);
        }

        /// <summary>
        /// Prepares a compute macro for execution.
        /// </summary>
        public void ComputeMmeStart(int index, int argument)
        {
            _computeMacros[index].StartExecution(_context, _parent, _computeMacroCode, argument, true);
        }

        /// <summary>
        /// Executes a compute macro.
        /// </summary>
        public void ComputeCallMme(int index, IDeviceState state)
        {
            _computeMacros[index].Execute(_computeMacroCode, state);
        }
    }
}
