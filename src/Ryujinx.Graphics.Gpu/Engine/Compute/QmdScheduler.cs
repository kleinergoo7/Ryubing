using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Schedules QMD 3.0 (Turing/Ampere) compute grids.
    /// </summary>
    /// <remarks>
    /// NVN2 tracks QMD completion with PCAS actions rather than host1x syncpoints: a grid is
    /// loaded and scheduled, later launches decrement its dependency count, and a grid runs
    /// once its count reaches zero. A finished grid fires its release slots and may chain the
    /// next QMD of a ring.
    /// </remarks>
    class QmdScheduler
    {
        private const int MaxStates = 4096;

        // Release slots (address low, flags | address high, payload). NVN2 patches a third slot
        // at word 52 for queue-to-queue syncs.
        private static readonly int[] ReleaseSlots = { 24, 28, 52 };

        // One table for every channel: a QMD's dependency counter belongs to the QMD, and one
        // channel may schedule it while another decrements it.
        private static readonly Dictionary<ulong, State> States = new();

        private static readonly object Lock = new();

        private struct State
        {
            public int[] Descriptor;
            public int Dependencies;
            public int Deferred;
            public bool Loaded;
            public bool Scheduled;
            public bool Completed;
        }

        private struct Pending
        {
            public ulong Address;
            public uint Action;
            public int Count;
        }

        /// <summary>
        /// Reads a QMD descriptor.
        /// </summary>
        public delegate bool ReadQmd(ulong address, Span<int> descriptor);

        /// <summary>
        /// Releases a value into memory.
        /// </summary>
        public delegate bool ReleaseQmd(ulong address, uint payload, uint flags);

        /// <summary>
        /// Dispatches a ready grid.
        /// </summary>
        public delegate bool DispatchQmd(ReadOnlySpan<int> descriptor);

        /// <summary>
        /// Applies a PCAS action.
        /// </summary>
        /// <param name="address">QMD address (already shifted)</param>
        /// <param name="argument">PCAS control word</param>
        /// <param name="read">Reads a QMD descriptor</param>
        /// <param name="release">Writes a release value</param>
        /// <param name="dispatch">Runs a ready grid</param>
        /// <returns>An error message, or null on success</returns>
        public string Signal(ulong address, uint argument, ReadQmd read, ReleaseQmd release, DispatchQmd dispatch)
        {
            Pending[] pending = new Pending[256];
            int count = 1;

            pending[0] = new Pending
            {
                Address = address,
                Action = argument & 15,
                Count = (int)((argument >> 14) & 0x3ff) + 1,
            };

            lock (Lock)
            {
                for (int index = 0; index < count; index++)
                {
                    Pending request = pending[index];

                    if (States.Count >= MaxStates && !States.ContainsKey(request.Address))
                    {
                        // Finished QMDs with nothing deferred are history: drop them instead of
                        // stalling the channel.
                        List<ulong> done = null;

                        foreach (KeyValuePair<ulong, State> entry in States)
                        {
                            if (entry.Value.Completed && entry.Value.Deferred == 0)
                            {
                                (done ??= new List<ulong>()).Add(entry.Key);
                            }
                        }

                        if (done != null)
                        {
                            foreach (ulong key in done)
                            {
                                States.Remove(key);
                            }
                        }

                        if (States.Count >= MaxStates)
                        {
                            return "QMD cache limit";
                        }
                    }

                    if (!States.TryGetValue(request.Address, out State state))
                    {
                        state = new State { Descriptor = new int[64] };
                        States[request.Address] = state;
                    }

                    if (request.Action == 3 || request.Action == 2)
                    {
                        if (request.Action == 3 || !state.Loaded)
                        {
                            string error = LoadQmd(request.Address, ref state, read);

                            if (error != null)
                            {
                                return error;
                            }

                            state.Dependencies = (int)((uint)(state.Descriptor[4] >> 14) & 0xffff) + state.Deferred;
                            state.Deferred = 0;
                            state.Loaded = true;
                            state.Completed = false;
                        }

                        state.Scheduled = true;
                    }
                    else if (request.Action == 7 || request.Action == 12)
                    {
                        int delta = request.Action == 7 ? -request.Count : request.Count;

                        if (!state.Loaded || state.Completed)
                        {
                            state.Deferred += delta;
                            States[request.Address] = state;
                            continue;
                        }

                        state.Dependencies += delta;
                    }
                    else if (request.Action == 0 || request.Action == 1)
                    {
                        continue;
                    }
                    else
                    {
                        States[request.Address] = state;
                        return "unsupported PCAS action";
                    }

                    if (!state.Loaded || !state.Scheduled || state.Completed || state.Dependencies > 0)
                    {
                        States[request.Address] = state;
                        continue;
                    }

                    if (state.Dependencies < 0)
                    {
                        States[request.Address] = state;
                        return "QMD dependency underflow";
                    }

                    // The SDK patches release payloads while a scheduled QMD waits for its
                    // remaining dependencies, so fetch the descriptor again at execution.
                    string loadError = LoadQmd(request.Address, ref state, read);

                    if (loadError != null)
                    {
                        return loadError;
                    }

                    int[] qmd = state.Descriptor;

                    foreach (int slot in ReleaseSlots)
                    {
                        uint flags = (uint)qmd[slot + 1];

                        if ((flags & (1u << 23)) == 0)
                        {
                            continue;
                        }

                        bool add = (flags & (1u << 26)) != 0;

                        if ((flags & (0x3bu << 24)) != 0 || (flags >> 30) != 1 || (add && (flags & (7u << 20)) != 0))
                        {
                            States[request.Address] = state;
                            return "unsupported QMD release format";
                        }
                    }

                    // A program this backend cannot run is skipped, but the QMD still completes:
                    // its releases are what queues and the CPU wait on.
                    bool dispatched = qmd[12] == 0 || (qmd[13] & 0xffff) == 0 || (qmd[14] & 0xffff) == 0 || dispatch(qmd);

                    state.Completed = true;

                    foreach (int slot in ReleaseSlots)
                    {
                        uint flags = (uint)qmd[slot + 1];

                        if ((flags & (1u << 23)) == 0)
                        {
                            continue;
                        }

                        ulong destination = ((ulong)(flags & 0xff) << 32) | (uint)qmd[slot];
                        uint payload = (uint)qmd[slot + 2];

                        if (!release(destination, payload, flags))
                        {
                            States[request.Address] = state;
                            return "unmapped QMD release";
                        }
                    }

                    if ((qmd[16] & 1) != 0)
                    {
                        uint action = (uint)(qmd[16] >> 1) & 7;

                        if (action != 4 && action != 3 && action != 1)
                        {
                            States[request.Address] = state;
                            return "unsupported dependent QMD action";
                        }

                        if (count == pending.Length)
                        {
                            States[request.Address] = state;
                            return "QMD chain limit";
                        }

                        pending[count++] = new Pending
                        {
                            Address = (ulong)(uint)qmd[15] << 8,
                            Action = action == 4 ? 7u : action == 3 ? 3u : 2u,
                            Count = 1,
                        };
                    }

                    if ((qmd[16] & (1u << 5)) != 0)
                    {
                        States[request.Address] = state;
                        return "second dependent QMD unsupported";
                    }

                    States[request.Address] = state;

                    _ = dispatched;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads and validates a QMD 3.0 descriptor.
        /// </summary>
        private static string LoadQmd(ulong address, ref State state, ReadQmd read)
        {
            Span<int> descriptor = state.Descriptor;

            if (!read(address, descriptor))
            {
                return "unmapped QMD";
            }

            if ((descriptor[18] & 0xff) != 0x30 || (descriptor[4] & 0x100) != 0)
            {
                return "unsupported QMD version or queue";
            }

            return null;
        }
    }
}
