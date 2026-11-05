// SPDX-License-Identifier: MIT
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Engine.MME
{
    /// <summary>
    /// Interpreter for the Turing+ (MME64) macro instruction set used by Switch 2 drivers.
    /// </summary>
    /// <remarks>
    /// One instruction is 96 bits (three words). Each instruction drives two ALUs and two
    /// outputs; a macro call consumes words from its parameter FIFO and writes GPU methods.
    /// The semantics follow Mesa's mme_tu104 simulator.
    /// </remarks>
    class MacroTuring : IMacroEE
    {
        private const int RegisterCount = 24;
        private const int DataRamSize = 0xc00;
        private const int InstructionWordCount = 3;
        private const int MaxSteps = 1000000;

        private const int MemAddressA = 0x154;
        private const int MemAddressB = 0x155;
        private const int DataRamAddress = 0x156;
        private const int DmaRead = 0x157;
        private const int DmaReadFifoed = 0x158;

        private static int _failureLogs;

        /// <summary>
        /// Decoded instruction fields, reused while the words at its address stay the same.
        /// </summary>
        private struct Decoded
        {
            public int W0;
            public int W1;
            public int W2;
            public bool Valid;
            public uint[] Imm;
            public uint[] Op;
            public uint[] Dst;
            public uint[] SrcA;
            public uint[] SrcB;
            public uint[] OutMethod;
            public uint[] OutEmit;
            public uint PredReg;
            public uint PredMode;
            public uint MaskTrue;
            public uint MaskFalse;
            public bool End;
            public bool[] Load;
        }

        /// <summary>
        /// Arguments FIFO.
        /// </summary>
        public Queue<FifoWord> Fifo { get; } = new();

        private readonly MemoryManager _memoryManager;
        private readonly int[] _dataRam;
        private readonly Func<long> _codeGeneration;

        private Decoded[] _decoded = Array.Empty<Decoded>();

        public static int EmittedMethods;

        internal static int TolUninitReg;
        internal static int TolFifoUnderflow;
        internal static int TolBadSelector;
        internal static int TolOobEnd;

        private static int _selLogs;

        internal static readonly System.Collections.Generic.Dictionary<uint, int> EmittedHistogram = new();

        private static int _failProbe;
        public static int MacrosRun;

        /// <summary>
        /// Memoized macro-call replays vs full reinterpretations (Eglantine reports ~97%
        /// replay on title traffic; a low hit rate here means the interpreter burns the
        /// GPU thread instead of replaying).
        /// </summary>
        public static int MacroMemoHits;
        public static int MacroMemoMisses;

        /// <summary>Reads one MME data-RAM word (diagnostics: the QMD ring base lives at 0x27).</summary>

        /// <summary>Reads one MME data-RAM word (diagnostics: the QMD ring base lives at 0x27).</summary>
        public int DataRamWord(int address)
        {
            return (uint)address < (uint)_dataRam.Length ? _dataRam[address] : 0;
        }

        private bool _readRegisters;
        private bool _rereadDataRam;
        private bool _readMemory;
        private readonly List<(uint Address, uint Value)> _dataRamReads = new();
        private readonly List<(uint Address, uint Value)> _dataRamWrites = new();
        private readonly List<(uint Address, uint Value)> _writes = new();

        private bool _memoValid;
        private long _memoGeneration; private int[] _memoParameters = Array.Empty<int>();
        private (uint Address, uint Value)[] _memoDataRamReads = Array.Empty<(uint, uint)>();
        private (uint Address, uint Value)[] _memoDataRamWrites = Array.Empty<(uint, uint)>();
        private (uint Address, uint Value)[] _memoWrites = Array.Empty<(uint, uint)>();

        /// <summary>
        /// True when the last call read a GPU register (its result depends on live state).
        /// </summary>
        public bool ReadRegisters => _readRegisters;

        /// <summary>
        /// True when the last call read a data RAM word it had written itself.
        /// </summary>
        public bool RereadDataRam => _rereadDataRam;

        /// <summary>
        /// Data RAM words read by the last call, in order.
        /// </summary>
        public IReadOnlyList<(uint Address, uint Value)> DataRamReads => _dataRamReads;

        /// <summary>
        /// Data RAM words written by the last call, in order.
        /// </summary>
        public IReadOnlyList<(uint Address, uint Value)> DataRamWrites => _dataRamWrites;

        /// <summary>
        /// Creates a new instance of the Turing macro interpreter.
        /// </summary>
        /// <param name="memoryManager">Channel memory manager, for macro DMA reads</param>
        /// <param name="dataRam">Per-channel MME data RAM</param>
        /// <param name="codeGeneration">Returns a counter that changes when macro code is uploaded</param>
        public MacroTuring(MemoryManager memoryManager, int[] dataRam, Func<long> codeGeneration)
        {
            _memoryManager = memoryManager;
            _dataRam = dataRam;
            _codeGeneration = codeGeneration;
        }

        /// <summary>
        /// Executes a macro program until it exits.
        /// </summary>
        /// <param name="code">Packed macro code (three words per instruction)</param>
        /// <param name="state">Current GPU state</param>
        /// <param name="arg0">First argument passed to the program</param>
        public void Execute(ReadOnlySpan<int> code, IDeviceState state, int arg0)
        {
            System.Threading.Interlocked.Increment(ref MacrosRun);

            List<int> parameters = new(Fifo.Count + 1) { arg0 };

            foreach (FifoWord word in Fifo)
            {
                parameters.Add(word.Word);
            }

            Fifo.Clear();

            if (TryReplay(parameters, state))
            {
                System.Threading.Interlocked.Increment(ref MacroMemoHits);

                return;
            }

            System.Threading.Interlocked.Increment(ref MacroMemoMisses);

            _readRegisters = false;
            _rereadDataRam = false;
            _readMemory = false;
            _dataRamReads.Clear();
            _dataRamWrites.Clear();
            _writes.Clear();

            string error = Run(code, state, parameters);

            if (!string.IsNullOrEmpty(error))
            {
                if (_failureLogs++ < 16)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Turing macro failed: {error}.");
                }

                _memoValid = false;
                return;
            }

            if (!_readMemory && !_readRegisters && !_rereadDataRam)
            {
                StoreMemo(parameters);
            }
            else
            {
                _memoValid = false;
            }
        }

        /// <summary>
        /// Runs the program, returning an error message or null on success.
        /// </summary>
        private string Run(ReadOnlySpan<int> code, IDeviceState state, List<int> parameters)
        {
            uint[] regs = new uint[RegisterCount];
            uint[] load = new uint[2];
            uint[] alu = new uint[2];
            uint[] xs = new uint[2];
            uint[] ys = new uint[2];
            // r0 reads as zero and counts as initialized: it is a hardwired register in this ISA,
            // and faulting on it aborted every Switch 2 macro at its first instruction.
            uint initialized = 1;
            uint carry = 0;
            uint method = 0;
            uint increment = 0;
            int pc = 0;
            uint loopCount = 0;
            int loopStart = 0;
            int loopEnd = 0;
            int consumed = 0;
            bool endNext = false;
            bool stopped = false;
            string error = null;

            int instructionCount = code.Length / InstructionWordCount;

            if (_decoded.Length < instructionCount)
            {
                Array.Resize(ref _decoded, instructionCount);
            }

            void Fail(string message)
            {
                if (error == null && _failProbe++ < 24)
                {
                    Decoded dd = _decoded[pc];
                    string raw = $"{dd.W0:X8}:{dd.W1:X8}:{dd.W2:X8}";
                    Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                        $"NX2 mfail {message} pc={pc} raw={raw} op=({dd.Op[0]},{dd.Op[1]}) dst=({dd.Dst[0]},{dd.Dst[1]}) srca=({dd.SrcA[0]},{dd.SrcA[1]}) srcb=({dd.SrcB[0]},{dd.SrcB[1]}) selM=({dd.OutMethod[0]},{dd.OutMethod[1]}) selE=({dd.OutEmit[0]},{dd.OutEmit[1]}) imm=({dd.Imm[0]:X},{dd.Imm[1]:X}) alu=({alu[0]:X},{alu[1]:X})");
                }

                error ??= message;
            }

            for (int steps = 0; steps < MaxSteps; steps++)
            {
                if (pc < 0 || (pc + 1) * InstructionWordCount > code.Length)
                {
                    System.Text.StringBuilder w = new();

                    for (int i = 0; i < 6 && i * InstructionWordCount + 2 < code.Length; i++)
                    {
                        w.Append($"{code[i * InstructionWordCount]:X8}{code[i * InstructionWordCount + 1]:X8}{code[i * InstructionWordCount + 2]:X8} ");
                    }

                    // WORKAROUND: running past the last instruction of its buffer is how
                    // these programs end (the macro RAM after a body is zero, and the body's
                    // own end bit is not one the decoder knows). Counted in TolOobEnd.
                    System.Threading.Interlocked.Increment(ref TolOobEnd);
                    return null;
                }

                Decoded d = _decoded[pc];

                int w0 = code[pc * InstructionWordCount];
                int w1 = code[pc * InstructionWordCount + 1];
                int w2 = code[pc * InstructionWordCount + 2];

                if (!d.Valid || d.W0 != w0 || d.W1 != w1 || d.W2 != w2)
                {
                    // Words are decoded low-to-high as words[2..0]: this matches the
                    // proven Eglantine Turing decoder (adapted from Mesa mme_tu104).
                    d = Decode(w2, w1, w0);
                    _decoded[pc] = d;
                }

                Array.Clear(load);
                Array.Clear(alu);

                for (int lane = 0; lane < 2; lane++)
                {
                    if (d.Load[lane])
                    {
                        if (consumed >= parameters.Count)
                        {
                            // WORKAROUND: the console reads zero past the FIFO end; failing
                            // here aborted whole macros. Counted in TolFifoUnderflow.
                            System.Threading.Interlocked.Increment(ref TolFifoUnderflow);
                            load[lane] = 0;

                            continue;
                        }

                        load[lane] = (uint)parameters[consumed++];
                    }
                }

                if (error != null)
                {
                    break;
                }

                uint get(uint reg, int lane, ReadOnlySpan<uint> loadValues)
                {
                    if (reg < RegisterCount)
                    {
                        if ((initialized & (1u << (int)reg)) == 0)
                        {
                            // WORKAROUND: these programs read registers they never wrote; the
                            // console reads whatever the register file holds (zero on a fresh
                            // channel). Failing here aborted every macro. Counted below.
                            System.Threading.Interlocked.Increment(ref TolUninitReg);
                        }

                        return regs[reg];
                    }

                    switch (reg)
                    {
                        case 24:
                            return 0;
                        case 25:
                            return (uint)(int)(short)d.Imm[lane];
                        case 26:
                            return (uint)(int)(short)d.Imm[1 - lane];
                        case 27:
                            return (d.Imm[0] << 16) | d.Imm[1];
                        case 28:
                            return loadValues[0];
                        case 29:
                            return loadValues[1];
                        default:
                            Fail("invalid source register");
                            return 0;
                    }
                }

                uint mask = d.PredMode != 0
                    ? (get(d.PredReg, 0, load) != 0 ? d.MaskTrue : d.MaskFalse)
                    : 15u;

                Array.Clear(xs);
                Array.Clear(ys);

                for (int lane = 0; lane < 2; lane++)
                {
                    if ((mask & (1u << lane)) != 0)
                    {
                        xs[lane] = get(d.SrcA[lane], lane, load);
                        ys[lane] = get(d.SrcB[lane], lane, load);
                    }
                }
                int nextPc = pc + 1;
                bool branchCancelsEnd = false;

                for (int lane = 0; lane < 2 && error == null; lane++)
                {
                    if ((mask & (1u << lane)) == 0)
                    {
                        continue;
                    }

                    uint x = xs[lane];
                    uint y = ys[lane];
                    uint imm = d.Imm[lane];
                    uint value = 0;

                    switch (d.Op[lane])
                    {
                        case 0:
                            value = x + y;
                            carry = value < x ? 1u : 0u;
                            break;
                        case 1:
                            value = x + y + carry;
                            break;
                        case 2:
                            value = x - y;
                            carry = value > x ? 1u : 0u;
                            break;
                        case 3:
                            value = x - y - carry;
                            break;
                        case 4:
                        case 6:
                            {
                                ulong a = d.Op[lane] == 4 ? (ulong)(long)(int)x : x;
                                ulong b = d.Op[lane] == 4 ? (ulong)(long)(int)y : y;
                                ulong product = a * b;

                                value = (uint)product;
                                carry = (uint)(product >> 32);
                                break;
                            }
                        case 5:
                            value = carry;
                            break;
                        case 7:
                            // 0x1000 is the load barrier (mme_tu104_load_barrier); a sequential
                            // interpreter has nothing to wait for.
                            if (x != 0x1000)
                            {
                                Fail("unsupported extended operation");
                            }

                            break;
                        case 8:
                            value = (uint)BitOperations.LeadingZeroCount(x);
                            break;
                        case 9:
                            value = x << (int)(y & 31);
                            break;
                        case 10:
                            value = x >> (int)(y & 31);
                            break;
                        case 11:
                            value = (uint)((int)x >> (int)(y & 31));
                            break;
                        case 12:
                            value = x & y;
                            break;
                        case 13:
                            value = ~(x & y);
                            break;
                        case 14:
                            value = x | y;
                            break;
                        case 15:
                            value = x ^ y;
                            break;
                        case 16:
                            {
                                uint dest = (imm >> 10) & 63;
                                uint count = (imm >> 5) & 31;
                                uint source = imm & 31;

                                if (dest >= 32)
                                {
                                    Fail("invalid merge destination");
                                    break;
                                }

                                uint bitsMask = count == 0 ? 0 : (1u << (int)count) - 1;

                                value = (x & ~(bitsMask << (int)dest)) | (((y >> (int)source) & bitsMask) << (int)dest);
                                break;
                            }
                        case 17:
                        case 18:
                        case 19:
                        case 20:
                        case 21:
                            value = Compare(d.Op[lane], x, y) ? uint.MaxValue : 0;
                            break;
                        case 22:
                            value = (uint)state.Read((int)((x + y) & 0xffff) * 4);
                            _readRegisters = true;
                            break;
                        case 23:
                            if (loopCount != 0 || imm < 2)
                            {
                                Fail("invalid loop");
                                break;
                            }

                            // The SDK emits zero-count loops around FIFO reads; skip the body.
                            if (x == 0)
                            {
                                nextPc = pc + (int)imm;
                                break;
                            }

                            loopCount = x - 1;
                            loopStart = pc;
                            loopEnd = pc + (int)imm - 1;
                            break;
                        case 24:
                            nextPc = pc + (int)(imm & 0x7fff);
                            break;
                        case 25:
                        case 26:
                        case 27:
                        case 28:
                        case 29:
                            if (Compare(d.Op[lane], x, y) == ((imm & 0x8000) != 0))
                            {
                                int offset = (int)(imm << 19) >> 19;

                                if (offset == -4096)
                                {
                                    stopped = true;
                                }
                                else
                                {
                                    nextPc = pc + offset;

                                    // Bit 14 suppresses the fallthrough termination when taken.
                                    branchCancelsEnd = (imm & 0x4000) != 0;
                                }
                            }
                            break;
                        case 30:
                            if (x >= DataRamSize)
                            {
                                Fail("data RAM read out of bounds");
                                break;
                            }

                            value = (uint)_dataRam[x];

                            foreach ((uint address, _) in _dataRamWrites)
                            {
                                if (address == x)
                                {
                                    _rereadDataRam = true;
                                    break;
                                }
                            }

                            _dataRamReads.Add((x, value));
                            break;
                        case 31:
                            if (x >= DataRamSize)
                            {
                                Fail("data RAM write out of bounds");
                                break;
                            }

                            _dataRam[(int)x] = (int)y;
                            _dataRamWrites.Add((x, y));
                            break;
                        default:
                            Fail("invalid ALU operation");
                            break;
                    }

                    alu[lane] = value;

                    if (d.Dst[lane] < RegisterCount)
                    {
                        regs[d.Dst[lane]] = value;
                        initialized |= 1u << (int)d.Dst[lane];
                    }
                    else if (d.Dst[lane] != 24)
                    {
                        Fail("invalid destination register");
                    }
                }

                if (error != null)
                {
                    break;
                }

                uint Output(uint selector, ReadOnlySpan<uint> aluValues, ReadOnlySpan<uint> loadValues)
                {
                    switch (selector)
                    {
                        case 1:
                            return aluValues[0];
                        case 2:
                            return aluValues[1];
                        case 3:
                            return loadValues[0];
                        case 4:
                            return loadValues[1];
                        case 5:
                            return d.Imm[0];
                        case 6:
                            return d.Imm[1];
                        case 8:
                            return d.Imm[0] >> 12;
                        case 9:
                            return d.Imm[1] >> 12;
                        case 10:
                            return (d.Imm[0] << 16) | d.Imm[1];
                        default:
                            // WORKAROUND: Mesa defines output selectors 0-10 only, but these
                            // macros address selectors 7 and 11-15. Emitting nothing keeps the
                            // macro alive; every trigger is logged by the mfail probe.
                            System.Threading.Interlocked.Increment(ref TolBadSelector);
                            return 0;
                    }
                }

                for (int lane = 0; lane < 2 && error == null; lane++)
                {
                    if ((mask & (4u << lane)) == 0)
                    {
                        continue;
                    }

                    if (d.OutMethod[lane] != 0)
                    {
                        uint address = Output(d.OutMethod[lane], alu, load);

                        method = address & 0xfff;
                        increment = (address >> 12) & 15;
                    }

                    if (error != null)
                    {
                        break;
                    }

                    if (d.OutEmit[lane] != 0)
                    {
                        uint value = Output(d.OutEmit[lane], alu, load);

                        if (System.Environment.GetEnvironmentVariable("NX2_MACRO_LOG") == "1")
                        {
                            int n = System.Threading.Interlocked.Increment(ref _selLogs);

                            if (n <= 40)
                            {
                                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                                    $"NX2 sel pc={pc} lane={lane} selM={d.OutMethod[lane]} selE={d.OutEmit[lane]} addr=0x{method:X} val=0x{value:X8} alu=({alu[0]:X},{alu[1]:X}) load=({load[0]:X},{load[1]:X}) imm=({d.Imm[0]:X},{d.Imm[1]:X})");
                            }
                        }

                        if (!Emit(state, method, value, parameters))
                        {
                            Fail("unsupported emitted method");
                        }

                        method += increment;
                    }
                }

                if (error != null)
                {
                    break;
                }

                if (endNext || stopped)
                {
                    return null;
                }

                endNext = d.End && !branchCancelsEnd;

                if (loopCount != 0 && pc == loopEnd)
                {
                    loopCount--;
                    nextPc = loopStart + 1;
                }

                pc = nextPc;
            }

            return error ?? "instruction limit exceeded";
        }

        /// <summary>
        /// Evaluates an encoded comparison.
        /// </summary>
        private static bool Compare(uint comparison, uint x, uint y)
        {
            return comparison switch
            {
                17 or 25 => (int)x < (int)y,
                18 or 26 => x < y,
                19 or 27 => (int)x <= (int)y,
                20 or 28 => x <= y,
                _ => x == y,
            };
        }

        /// <summary>
        /// Applies one GPU method write, including the memory accesses the MME itself performs.
        /// </summary>
        private bool Emit(IDeviceState state, uint address, uint value, List<int> parameters)
        {
            if (address >= 0xe00)
            {
                return false;
            }

            int emitIndex = System.Threading.Interlocked.Increment(ref EmittedMethods);

            lock (EmittedHistogram)
            {
                EmittedHistogram.TryGetValue(address, out int seen);
                EmittedHistogram[address] = seen + 1;
            }

            if (System.Environment.GetEnvironmentVariable("NX2_MACRO_LOG") == "1" && emitIndex <= 30)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"NX2 emit addr=0x{address:X} value=0x{value:X8}");
            }

            state.Write((int)address * 4, (int)value);
            _writes.Add((address, value));

            if (address == DmaRead || address == DmaReadFifoed)
            {
                _readMemory = true;

                ulong source = ((ulong)((uint)state.Read(MemAddressA * 4) & 0x1ffffff) << 32) |
                                (uint)state.Read(MemAddressB * 4);

                int ram = state.Read(DataRamAddress * 4);
                int words = Math.Min((int)value, 0x1000);

                try
                {
                    for (int index = 0; index < words; index++)
                    {
                        int word = _memoryManager.Read<int>(source + (ulong)index * 4);

                        if (address == DmaReadFifoed)
                        {
                            parameters.Add(word);
                        }
                        else if ((uint)(ram + index) < DataRamSize)
                        {
                            _dataRam[ram + index] = word;
                        }
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Replays the previous call's effects when its parameters and data RAM inputs match.
        /// </summary>
        private bool TryReplay(List<int> parameters, IDeviceState state)
        {
            if (!_memoValid || _codeGeneration() != _memoGeneration)
            {
                _memoValid = false;
                return false;
            }

            if (parameters.Count != _memoParameters.Length)
            {
                return false;
            }

            for (int index = 0; index < parameters.Count; index++)
            {
                if (parameters[index] != _memoParameters[index])
                {
                    return false;
                }
            }

            foreach ((uint address, uint value) in _memoDataRamReads)
            {
                if ((uint)_dataRam[address] != value)
                {
                    return false;
                }
            }

            foreach ((uint address, uint value) in _memoDataRamWrites)
            {
                _dataRam[address] = (int)value;
            }

            foreach ((uint address, uint value) in _memoWrites)
            {
                if (!Emit(state, address, value, parameters))
                {
                    _memoValid = false;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Remembers the last call when its effects depend only on its parameters and data RAM.
        /// </summary>
        private void StoreMemo(List<int> parameters)
        {
            _memoValid = true;
            _memoGeneration = _codeGeneration();
            _memoParameters = parameters.ToArray();
            _memoDataRamReads = _dataRamReads.ToArray();
            _memoDataRamWrites = _dataRamWrites.ToArray();
            _memoWrites = _writes.ToArray();
        }

        /// <summary>
        /// Decodes one 96-bit instruction.
        /// </summary>
        private static Decoded Decode(int w0, int w1, int w2)
        {
            uint[] words = { (uint)w0, (uint)w1, (uint)w2 };

            uint Field(int low, int count)
            {
                ulong bits = words[low / 32];

                if (low / 32 < 2)
                {
                    bits |= (ulong)words[low / 32 + 1] << 32;
                }

                return (uint)((bits >> (low % 32)) & ((1UL << count) - 1));
            }

            Decoded d = new()
            {
                W0 = w0,
                W1 = w1,
                W2 = w2,
                Valid = true,
                Imm = new[] { Field(30, 16), Field(66, 16) },
                Op = new[] { Field(10, 5), Field(46, 5) },
                Dst = new[] { Field(15, 5), Field(51, 5) },
                SrcA = new[] { Field(20, 5), Field(56, 5) },
                SrcB = new[] { Field(25, 5), Field(61, 5) },
                OutMethod = new[] { Field(82, 3), Field(89, 3) },
                OutEmit = new[] { Field(85, 4), Field(92, 4) },
                PredReg = Field(5, 5),
                PredMode = Field(1, 4),
                End = Field(0, 1) != 0,
                Load = new bool[2],
            };

            ReadOnlySpan<string> predicates = new[]
            {
                "UUUU", "TTTT", "FFFF", "TTUU", "FFUU", "TFUU", "TUUU", "FUUU",
                "UUTT", "UUTF", "UUTU", "UUFT", "UUFF", "UUFU", "UUUT", "UUUF",
            };

            d.MaskTrue = 15;
            d.MaskFalse = 15;

            if (d.PredMode != 0)
            {
                d.MaskTrue = 0;
                d.MaskFalse = 0;

                string predicate = predicates[(int)d.PredMode];

                for (int index = 0; index < 4; index++)
                {
                    if (predicate[index] != 'T')
                    {
                        d.MaskTrue |= 1u << index;
                    }

                    if (predicate[index] != 'F')
                    {
                        d.MaskFalse |= 1u << index;
                    }
                }
            }

            for (int lane = 0; lane < 2; lane++)
            {
                uint reg = (uint)(28 + lane);
                uint output = (uint)(3 + lane);

                d.Load[lane] =
                    d.PredReg == reg ||
                    d.SrcA[0] == reg || d.SrcA[1] == reg ||
                    d.SrcB[0] == reg || d.SrcB[1] == reg ||
                    d.OutMethod[0] == output || d.OutMethod[1] == output ||
                    d.OutEmit[0] == output || d.OutEmit[1] == output;
            }

            return d;
        }
    }
}
