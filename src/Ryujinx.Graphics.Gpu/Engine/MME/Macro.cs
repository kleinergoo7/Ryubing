using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.MME
{
    /// <summary>
    /// GPU macro program.
    /// </summary>
    struct Macro
    {
        /// <summary>
        /// Word offset of the code on the code memory.
        /// </summary>
        public int Position { get; }

        private IMacroEE _executionEngine;
        private bool _executionPending;
        private bool _turing;
        private int _argument;
        private MacroHLEFunctionName _hleFunction;
        // Args pushed before StartExecution created the engine (the guest pushes
        // them on odd method offsets, then starts on the even offset). Buffer them
        // here so they are not dropped by a null engine.
        private readonly List<FifoWord> _pendingArgs = new();

        /// <summary>
        /// Creates a new instance of the GPU cached macro program.
        /// </summary>
        /// <param name="position">Macro code start position</param>
        public Macro(int position)
        {
            Position = position;

            _executionEngine = null;
            _executionPending = false;
            _turing = false;
            _argument = 0;
            _hleFunction = MacroHLEFunctionName.None;
        }

        /// <summary>
        /// Sets the first argument for the macro call.
        /// </summary>
        /// <param name="context">GPU context where the macro code is being executed</param>
        /// <param name="processor">GPU GP FIFO command processor</param>
        /// <param name="code">Code to be executed</param>
        /// <param name="argument">First argument</param>
        /// <param name="compute">True for the compute engine's own macro code bank</param>
        public void StartExecution(GpuContext context, GPFifoProcessor processor, ReadOnlySpan<int> code, int argument, bool compute = false)
        {
            _argument = argument;

            _executionPending = true;

            if (_executionEngine == null)
            {
                if (context.IsNx2Ampere)
                {
                    // Switch 2 drivers upload the Turing+ (MME64) instruction set, which the
                    // Maxwell interpreters and HLE tables do not describe.
                    _executionEngine = new MacroTuring(
                        processor.MemoryManager,
                        processor.MmeDataRam,
                        compute ? () => processor.ComputeMmeCodeGeneration : () => processor.MmeCodeGeneration);
                    _turing = true;
                }
                else if (GraphicsConfig.EnableMacroHLE && MacroHLETable.TryGetMacroHLEFunction(code[Position..], context.Capabilities, out _hleFunction))
                {
                    _executionEngine = new MacroHLE(processor, _hleFunction);
                }
                else if (GraphicsConfig.EnableMacroJit)
                {
                    _executionEngine = new MacroJit();
                }
                else
                {
                    _executionEngine = new MacroInterpreter();
                }

                // Flush args that were pushed before the engine existed.
                foreach (FifoWord pending in _pendingArgs)
                {
                    _executionEngine.Fifo.Enqueue(pending);
                }
                _pendingArgs.Clear();
            }

            // We don't consume the parameter buffer value, so we don't need to flush it.
            // Doing so improves performance if the value was written by a GPU shader.
            if (_hleFunction == MacroHLEFunctionName.DrawElementsIndirect)
            {
                context.GPFifo.SetFlushSkips(1);
            }
            else if (_hleFunction == MacroHLEFunctionName.MultiDrawElementsIndirectCount)
            {
                context.GPFifo.SetFlushSkips(2);
            }
        }

        /// <summary>
        /// Starts executing the macro program code.
        /// </summary>
        /// <param name="code">Program code</param>
        /// <param name="state">Current GPU state</param>
        public void Execute(ReadOnlySpan<int> code, IDeviceState state)
        {
            if (_executionPending)
            {
                _executionPending = false;

                if (System.Environment.GetEnvironmentVariable("NX2_MACRO_LOG") == "1" && _turing)
                {
                    // Word 0x27 holds the QMD ring base on Switch 2 drivers; it must differ
                    // per channel (shared RAM drove every channel at the same ring on Eden).
                    int ringBase = _executionEngine is MacroTuring turing ? turing.DataRamWord(0x27) : 0;

                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"NX2 mme exec pos={Position} dram27=0x{ringBase:X8}");
                }

                if (_turing)
                {
                    // Turing positions are instruction indices; the code array stores three
                    // words per instruction.
                    int wordOffset = Position * 3;

                    _executionEngine?.Execute(
                        (uint)wordOffset <= (uint)code.Length ? code[wordOffset..] : ReadOnlySpan<int>.Empty,
                        state,
                        _argument);
                }
                else
                {
                    _executionEngine?.Execute(code[Position..], state, _argument);
                }
            }
        }

        /// <summary>
        /// Pushes an argument to the macro call argument FIFO.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="argument">Argument to be pushed</param>
        public readonly void PushArgument(ulong gpuVa, int argument)
        {
            if (_executionEngine == null)
            {
                _pendingArgs.Add(new FifoWord(gpuVa, argument));
            }
            else
            {
                _executionEngine.Fifo.Enqueue(new FifoWord(gpuVa, argument));
            }
        }
    }
}
