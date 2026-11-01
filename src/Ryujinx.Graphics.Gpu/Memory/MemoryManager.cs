using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// GPU memory manager.
    /// </summary>
    public class MemoryManager : IWritableBlock
    {
        private const int PtLvl0Bits = 14;
        private const int PtLvl1Bits = 14;
        public const int PtPageBits = 12;

        private const ulong PtLvl0Size = 1UL << PtLvl0Bits;
        private const ulong PtLvl1Size = 1UL << PtLvl1Bits;
        public const ulong PageSize = 1UL << PtPageBits;

        private const ulong PtLvl0Mask = PtLvl0Size - 1;
        private const ulong PtLvl1Mask = PtLvl1Size - 1;
        public const ulong PageMask = PageSize - 1;

        private const int PtLvl0Bit = PtPageBits + PtLvl1Bits;
        private const int PtLvl1Bit = PtPageBits;
        private const int AddressSpaceBits = PtPageBits + PtLvl1Bits + PtLvl0Bits;

        public const ulong PteUnmapped = ulong.MaxValue;

        /// <summary>
        /// NX2 GPU virtual addresses are the process CPU virtual addresses of the same range,
        /// so unmapped pages resolve to themselves instead of failing.
        /// Also enables the NX2 hooks: <see cref="Nx2GpuMemory"/> mapping/sparse resolution and
        /// the queue semaphore page mirror on writes.
        /// </summary>
        public bool Nx2IdentityMapping { get; set; }

        /// <summary>
        /// GPU address of the NX2 syncpoint shim page, or zero when unused.
        /// Reads of 32-bit values there return live host1x syncpoint counters.
        /// </summary>
        public ulong Nx2SyncpointShim { get; set; }

        /// <summary>
        /// Reads a host1x syncpoint counter for the NX2 syncpoint shim.
        /// </summary>
        public Func<uint, uint> Nx2SyncpointReader { get; set; }

        private readonly ulong[][] _pageTable;

        public event EventHandler<UnmapEventArgs> MemoryUnmapped;

        /// <summary>
        /// Physical memory where the virtual memory is mapped into.
        /// </summary>
        internal PhysicalMemory Physical { get; }

        /// <summary>
        /// Virtual range cache.
        /// </summary>
        internal VirtualRangeCache VirtualRangeCache { get; }

        /// <summary>
        /// Cache of GPU counters.
        /// </summary>
        internal CounterCache CounterCache { get; }
        
        private delegate void WriteCallback(ulong address, ReadOnlySpan<byte> data);

        private WriteCallback _write;
        private WriteCallback _writeTrackedResource;
        private WriteCallback _writeUntracked;

        /// <summary>
        /// Creates a new instance of the GPU memory manager.
        /// </summary>
        /// <param name="physicalMemory">Physical memory that this memory manager will map into</param>
        /// <param name="cpuMemorySize">The amount of physical CPU Memory Avaiable on the device.</param>
        internal MemoryManager(PhysicalMemory physicalMemory, ulong cpuMemorySize)
        {
            Physical = physicalMemory;
            _write = physicalMemory.Write;
            _writeTrackedResource = physicalMemory.WriteTrackedResource;
            _writeUntracked = physicalMemory.WriteUntracked;
            VirtualRangeCache = new VirtualRangeCache(this);
            CounterCache = new CounterCache();
            _pageTable = new ulong[PtLvl0Size][];
            MemoryUnmapped += Physical.TextureCache.MemoryUnmappedHandler;
            MemoryUnmapped += Physical.BufferCache.MemoryUnmappedHandler;
            MemoryUnmapped += VirtualRangeCache.MemoryUnmappedHandler;
            MemoryUnmapped += CounterCache.MemoryUnmappedHandler;
            Physical.TextureCache.Initialize(cpuMemorySize);
        }

        /// <summary>
        /// Reads data from GPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data</typeparam>
        /// <param name="va">GPU virtual address where the data is located</param>
        /// <param name="tracked">True if read tracking is triggered on the memory region</param>
        /// <returns>The data at the specified memory location</returns>
        public T Read<T>(ulong va, bool tracked = false) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();

            if (size == sizeof(uint) && Nx2SyncpointShim != 0 && Nx2SyncpointReader != null)
            {
                ulong shimOffset = va - Nx2SyncpointShim;

                if (va >= Nx2SyncpointShim && shimOffset < 192 * sizeof(uint) && (shimOffset & 3) == 0)
                {
                    uint value = Nx2SyncpointReader((uint)(shimOffset / sizeof(uint)));

                    return Unsafe.As<uint, T>(ref value);
                }
            }

            if (Nx2IdentityMapping && Nx2GpuMemory.IsSparse(va) && Nx2GpuMemory.TryReadSparse(va, out T sparseValue))
            {
                return sparseValue;
            }

            if (IsContiguous(va, size))
            {
                ulong address = Translate(va);

                if (tracked)
                {
                    return Physical.ReadTracked<T>(address);
                }
                else
                {
                    return Physical.Read<T>(address);
                }
            }
            else
            {
                Span<byte> data = new byte[size];

                ReadImpl(va, data, tracked);

                return MemoryMarshal.Cast<byte, T>(data)[0];
            }
        }

        /// <summary>
        /// Gets a read-only span of data from GPU mapped memory.
        /// </summary>
        /// <param name="va">GPU virtual address where the data is located</param>
        /// <param name="size">Size of the data</param>
        /// <param name="tracked">True if read tracking is triggered on the span</param>
        /// <returns>The span of the data at the specified memory location</returns>
        public ReadOnlySpan<byte> GetSpan(ulong va, int size, bool tracked = false)
        {
            if (Nx2IdentityMapping && Nx2GpuMemory.IsSparse(va))
            {
                Span<byte> sparse = new byte[size];

                Nx2GpuMemory.TryReadSparse(va, sparse);

                return sparse;
            }

            if (IsContiguous(va, size))
            {
                return Physical.GetSpan(Translate(va), size, tracked);
            }
            else
            {
                Span<byte> data = new byte[size];

                ReadImpl(va, data, tracked);

                return data;
            }
        }

        /// <summary>
        /// Gets a read-only span of data from GPU mapped memory, up to the entire range specified,
        /// or the last mapped page if the range is not fully mapped.
        /// </summary>
        /// <param name="va">GPU virtual address where the data is located</param>
        /// <param name="size">Size of the data</param>
        /// <param name="tracked">True if read tracking is triggered on the span</param>
        /// <returns>The span of the data at the specified memory location</returns>
        public ReadOnlySpan<byte> GetSpanMapped(ulong va, int size, bool tracked = false)
        {
            if (Nx2IdentityMapping && Nx2GpuMemory.IsSparse(va))
            {
                return GetSpan(va, size, tracked);
            }

            bool isContiguous = true;
            int mappedSize;

            if (ValidateAddress(va) && GetPte(va) != PteUnmapped && Physical.IsMapped(Translate(va)))
            {
                ulong endVa = va + (ulong)size;
                ulong endVaAligned = (endVa + PageMask) & ~PageMask;
                ulong currentVa = va & ~PageMask;

                int pages = (int)((endVaAligned - currentVa) / PageSize);

                for (int page = 0; page < pages - 1; page++)
                {
                    ulong nextVa = currentVa + PageSize;
                    ulong nextPa = Translate(nextVa);

                    if (!ValidateAddress(nextVa) || GetPte(nextVa) == PteUnmapped || !Physical.IsMapped(nextPa))
                    {
                        break;
                    }

                    if (Translate(currentVa) + PageSize != nextPa)
                    {
                        isContiguous = false;
                    }

                    currentVa += PageSize;
                }

                currentVa += PageSize;

                if (currentVa > endVa)
                {
                    currentVa = endVa;
                }

                mappedSize = (int)(currentVa - va);
            }
            else
            {
                return ReadOnlySpan<byte>.Empty;
            }

            if (isContiguous)
            {
                return Physical.GetSpan(Translate(va), mappedSize, tracked);
            }
            else
            {
                Span<byte> data = new byte[mappedSize];

                ReadImpl(va, data, tracked);

                return data;
            }
        }

        /// <summary>
        /// Reads data from a possibly non-contiguous region of GPU mapped memory.
        /// </summary>
        /// <param name="va">GPU virtual address of the data</param>
        /// <param name="data">Span to write the read data into</param>
        /// <param name="tracked">True to enable write tracking on read, false otherwise</param>
        private void ReadImpl(ulong va, Span<byte> data, bool tracked)
        {
            if (data.Length == 0)
            {
                return;
            }

            int offset = 0, size;

            if ((va & PageMask) != 0)
            {
                ulong pa = Translate(va);

                size = Math.Min(data.Length, (int)PageSize - (int)(va & PageMask));

                Physical.GetSpan(pa, size, tracked).CopyTo(data[..size]);

                offset += size;
            }

            for (; offset < data.Length; offset += size)
            {
                ulong pa = Translate(va + (ulong)offset);

                size = Math.Min(data.Length - offset, (int)PageSize);

                Physical.GetSpan(pa, size, tracked).CopyTo(data.Slice(offset, size));
            }
        }

        /// <summary>
        /// Gets a writable region from GPU mapped memory.
        /// </summary>
        /// <param name="va">Start address of the range</param>
        /// <param name="size">Size in bytes to be range</param>
        /// <param name="tracked">True if write tracking is triggered on the span</param>
        /// <returns>A writable region with the data at the specified memory location</returns>
        public WritableRegion GetWritableRegion(ulong va, int size, bool tracked = false)
        {
            if (Nx2IdentityMapping && Nx2GpuMemory.IsSparse(va))
            {
                MemoryOwner<byte> sparseOwner = MemoryOwner<byte>.Rent(size);

                Nx2GpuMemory.TryReadSparse(va, sparseOwner.Span);

                // Dispose writes the copy back through Write/WriteUntracked, which lands in the
                // sparse region again.
                return new WritableRegion(this, va, sparseOwner, tracked);
            }

            if (IsContiguous(va, size))
            {
                return Physical.GetWritableRegion(Translate(va), size, tracked);
            }
            else
            {
                MemoryOwner<byte> memoryOwner = MemoryOwner<byte>.Rent(size);

                ReadImpl(va, memoryOwner.Span, tracked);

                return new WritableRegion(this, va, memoryOwner, tracked);
            }
        }

        /// <summary>
        /// Writes data to GPU mapped memory.
        /// </summary>
        /// <typeparam name="T">Type of the data</typeparam>
        /// <param name="va">GPU virtual address to write the value into</param>
        /// <param name="value">The value to be written</param>
        public void Write<T>(ulong va, T value) where T : unmanaged
        {
            Write(va, MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref value, 1)));
        }

        /// <summary>
        /// Writes data to GPU mapped memory.
        /// </summary>
        /// <param name="va">GPU virtual address to write the data into</param>
        /// <param name="data">The data to be written</param>
        public void Write(ulong va, ReadOnlySpan<byte> data)
        {
            WriteImpl(va, data, _write);
        }

        /// <summary>
        /// Writes data to GPU mapped memory, destined for a tracked resource.
        /// </summary>
        /// <param name="va">GPU virtual address to write the data into</param>
        /// <param name="data">The data to be written</param>
        public void WriteTrackedResource(ulong va, ReadOnlySpan<byte> data)
        {
            WriteImpl(va, data, _writeTrackedResource);
        }

        /// <summary>
        /// Writes data to GPU mapped memory without write tracking.
        /// </summary>
        /// <param name="va">GPU virtual address to write the data into</param>
        /// <param name="data">The data to be written</param>
        public void WriteUntracked(ulong va, ReadOnlySpan<byte> data)
        {
            WriteImpl(va, data, _writeUntracked);
        }

        /// <summary>
        /// Writes data to possibly non-contiguous GPU mapped memory.
        /// </summary>
        /// <param name="va">GPU virtual address of the region to write into</param>
        /// <param name="data">Data to be written</param>
        /// <param name="writeCallback">Write callback</param>
        private void WriteImpl(ulong va, ReadOnlySpan<byte> data, WriteCallback writeCallback)
        {
            if (Nx2IdentityMapping)
            {
                if (Nx2GpuMemory.IsSparse(va) && Nx2GpuMemory.TryWriteSparse(va, data))
                {
                    return;
                }

                MirrorSemaphores(va, data, writeCallback);
            }

            if (IsContiguous(va, data.Length))
            {
                writeCallback(Translate(va), data);
            }
            else
            {
                int offset = 0, size;

                if ((va & PageMask) != 0)
                {
                    ulong pa = Translate(va);

                    size = Math.Min(data.Length, (int)PageSize - (int)(va & PageMask));

                    writeCallback(pa, data[..size]);

                    offset += size;
                }

                for (; offset < data.Length; offset += size)
                {
                    ulong pa = Translate(va + (ulong)offset);

                    size = Math.Min(data.Length - offset, (int)PageSize);

                    writeCallback(pa, data.Slice(offset, size));
                }
            }
        }

        /// <summary>
        /// Mirrors GPU writes into the second page of a queue semaphore region to the first page.
        /// The guest queues release into page two and wait on page one at the same offset.
        /// </summary>
        /// <param name="va">GPU virtual address of the write</param>
        /// <param name="data">Data written</param>
        /// <param name="writeCallback">Write callback to use for the mirror</param>
        private void MirrorSemaphores(ulong va, ReadOnlySpan<byte> data, WriteCallback writeCallback)
        {
            ReadOnlySpan<ulong> regions = Nx2GpuMemory.SemaphoreRegions;

            for (int i = 0; i < regions.Length; i++)
            {
                ulong second = regions[i] + 0x1000;
                ulong end = va + (ulong)data.Length;

                if (va >= second + 0x1000 || end <= second)
                {
                    continue;
                }

                ulong from = Math.Max(va, second);
                ulong to = Math.Min(end, second + 0x1000);

                writeCallback(Translate(from - 0x1000), data.Slice((int)(from - va), (int)(to - from)));
            }
        }

        /// <summary>
        /// Runs remap actions that are added to an unmap event.
        /// These must run after the mapping completes.
        /// </summary>
        /// <param name="e">Event with remap actions</param>
        private static void RunRemapActions(UnmapEventArgs e)
        {
            if (e.RemapActions != null)
            {
                foreach (Action action in e.RemapActions)
                {
                    action();
                }
            }
        }

        /// <summary>
        /// Maps a given range of pages to the specified CPU virtual address.
        /// </summary>
        /// <remarks>
        /// All addresses and sizes must be page aligned.
        /// </remarks>
        /// <param name="pa">CPU virtual address to map into</param>
        /// <param name="va">GPU virtual address to be mapped</param>
        /// <param name="size">Size in bytes of the mapping</param>
        /// <param name="kind">Kind of the resource located at the mapping</param>
        public void Map(ulong pa, ulong va, ulong size, PteKind kind)
        {
            lock (_pageTable)
            {
                UnmapEventArgs e = new(va, size);
                MemoryUnmapped?.Invoke(this, e);

                for (ulong offset = 0; offset < size; offset += PageSize)
                {
                    SetPte(va + offset, PackPte(pa + offset, kind));
                }

                RunRemapActions(e);
            }
        }

        /// <summary>
        /// Unmaps a given range of pages at the specified GPU virtual memory region.
        /// </summary>
        /// <param name="va">GPU virtual address to unmap</param>
        /// <param name="size">Size in bytes of the region being unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            lock (_pageTable)
            {
                // Event handlers are not expected to be thread safe.
                UnmapEventArgs e = new(va, size);
                MemoryUnmapped?.Invoke(this, e);

                for (ulong offset = 0; offset < size; offset += PageSize)
                {
                    SetPte(va + offset, PteUnmapped);
                }

                RunRemapActions(e);
            }
        }

        /// <summary>
        /// Checks if a region of GPU mapped memory is contiguous.
        /// </summary>
        /// <param name="va">GPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <returns>True if the region is contiguous, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsContiguous(ulong va, int size)
        {
            if (!ValidateAddress(va) || GetPte(va) == PteUnmapped)
            {
                return false;
            }

            ulong endVa = (va + (ulong)size + PageMask) & ~PageMask;

            va &= ~PageMask;

            int pages = (int)((endVa - va) / PageSize);

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize) || GetPte(va + PageSize) == PteUnmapped)
                {
                    return false;
                }

                if (Translate(va) + PageSize != Translate(va + PageSize))
                {
                    return false;
                }

                va += PageSize;
            }

            return true;
        }

        /// <summary>
        /// Gets the physical regions that make up the given virtual address region.
        /// </summary>
        /// <param name="va">Virtual address of the range</param>
        /// <param name="size">Size of the range</param>
        /// <returns>Multi-range with the physical regions</returns>
        public MultiRange GetPhysicalRegions(ulong va, ulong size)
        {
            if (IsContiguous(va, (int)size))
            {
                return new MultiRange(Translate(va), size);
            }

            ulong regionStart = Translate(va);
            ulong regionSize = Math.Min(size, PageSize - (va & PageMask));

            ulong endVa = va + size;
            ulong endVaRounded = (endVa + PageMask) & ~PageMask;

            va &= ~PageMask;

            int pages = (int)((endVaRounded - va) / PageSize);

            List<MemoryRange> regions = [];

            for (int page = 0; page < pages - 1; page++)
            {
                ulong currPa = Translate(va);
                ulong newPa = Translate(va + PageSize);

                if ((currPa != PteUnmapped || newPa != PteUnmapped) && currPa + PageSize != newPa)
                {
                    regions.Add(new MemoryRange(regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += Math.Min(endVa - va, PageSize);
            }

            if (regions.Count == 0)
            {
                return new MultiRange(regionStart, regionSize);
            }

            regions.Add(new MemoryRange(regionStart, regionSize));

            return new MultiRange(regions.ToArray());
        }

        /// <summary>
        /// Checks if a given GPU virtual memory range is mapped to the same physical regions
        /// as the specified physical memory multi-range.
        /// </summary>
        /// <param name="range">Physical memory multi-range</param>
        /// <param name="va">GPU virtual memory address</param>
        /// <returns>True if the virtual memory region is mapped into the specified physical one, false otherwise</returns>
        public bool CompareRange(MultiRange range, ulong va)
        {
            va &= ~PageMask;

            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange currentRange = range.GetSubRange(i);

                if (currentRange.Address != PteUnmapped)
                {
                    ulong address = currentRange.Address & ~PageMask;
                    ulong endAddress = (currentRange.EndAddress + PageMask) & ~PageMask;

                    while (address < endAddress)
                    {
                        if (Translate(va) != address)
                        {
                            return false;
                        }

                        va += PageSize;
                        address += PageSize;
                    }
                }
                else
                {
                    ulong endVa = va + (((currentRange.Size) + PageMask) & ~PageMask);

                    while (va < endVa)
                    {
                        if (Translate(va) != PteUnmapped)
                        {
                            return false;
                        }

                        va += PageSize;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Validates a GPU virtual address.
        /// </summary>
        /// <param name="va">Address to validate</param>
        /// <returns>True if the address is valid, false otherwise</returns>
        private static bool ValidateAddress(ulong va)
        {
            return va < (1UL << AddressSpaceBits);
        }

        /// <summary>
        /// Checks if a given page is mapped.
        /// </summary>
        /// <param name="va">GPU virtual address of the page to check</param>
        /// <returns>True if the page is mapped, false otherwise</returns>
        public bool IsMapped(ulong va)
        {
            return Translate(va) != PteUnmapped;
        }

        /// <summary>
        /// Translates a GPU virtual address to a CPU virtual address.
        /// </summary>
        /// <param name="va">GPU virtual address to be translated</param>
        /// <returns>CPU virtual address, or <see cref="PteUnmapped"/> if unmapped</returns>
        public ulong Translate(ulong va)
        {
            if (!ValidateAddress(va))
            {
                return PteUnmapped;
            }

            ulong pte = GetPte(va);

            if (pte != PteUnmapped)
            {
                return UnpackPaFromPte(pte) + (va & PageMask);
            }

            if (!Nx2IdentityMapping)
            {
                return PteUnmapped;
            }

            // Sparse regions are host allocations, not guest memory: the direct read/write paths
            // in this class handle them, and everything else sees them as unmapped.
            if (Nx2GpuMemory.IsSparse(va))
            {
                return PteUnmapped;
            }

            if (Nx2GpuMemory.TryResolveMapping(va, out ulong mapped))
            {
                return mapped + (va & PageMask);
            }

            return va;
        }

        /// <summary>
        /// Translates a GPU virtual address to a CPU virtual address on the first mapped page of memory
        /// on the specified region.
        /// If no page is mapped on the specified region, <see cref="PteUnmapped"/> is returned.
        /// </summary>
        /// <param name="va">GPU virtual address to be translated</param>
        /// <param name="size">Size of the range to be translated</param>
        /// <returns>CPU virtual address, or <see cref="PteUnmapped"/> if unmapped</returns>
        public ulong TranslateFirstMapped(ulong va, ulong size)
        {
            if (!ValidateAddress(va))
            {
                return PteUnmapped;
            }

            ulong endVa = va + size;

            ulong pte = GetPte(va);

            for (; va < endVa && pte == PteUnmapped; va += PageSize - (va & PageMask))
            {
                pte = GetPte(va);
            }

            if (pte == PteUnmapped)
            {
                return PteUnmapped;
            }

            return UnpackPaFromPte(pte) + (va & PageMask);
        }

        /// <summary>
        /// Translates a GPU virtual address and returns the number of bytes that are mapped after it.
        /// </summary>
        /// <param name="va">GPU virtual address to be translated</param>
        /// <param name="maxSize">Maximum size in bytes to scan</param>
        /// <returns>Number of bytes, 0 if unmapped</returns>
        public ulong GetMappedSize(ulong va, ulong maxSize)
        {
            if (!ValidateAddress(va))
            {
                return 0;
            }

            ulong startVa = va;
            ulong endVa = va + maxSize;

            ulong pte = GetPte(va);

            while (pte != PteUnmapped && va < endVa)
            {
                va += PageSize - (va & PageMask);
                pte = GetPte(va);
            }

            return Math.Min(maxSize, va - startVa);
        }

        /// <summary>
        /// Gets the kind of a given memory page.
        /// This might indicate the type of resource that can be allocated on the page, and also texture tiling.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <returns>Kind of the memory page</returns>
        public PteKind GetKind(ulong va)
        {
            if (!ValidateAddress(va))
            {
                return PteKind.Invalid;
            }

            ulong pte = GetPte(va);

            if (pte == PteUnmapped)
            {
                return PteKind.Invalid;
            }

            return UnpackKindFromPte(pte);
        }

        /// <summary>
        /// Gets the Page Table entry for a given GPU virtual address.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <returns>Page table entry (CPU virtual address)</returns>
        private ulong GetPte(ulong va)
        {
            ulong l0 = (va >> PtLvl0Bit) & PtLvl0Mask;
            ulong l1 = (va >> PtLvl1Bit) & PtLvl1Mask;

            if (_pageTable[l0] == null)
            {
                return PteUnmapped;
            }

            return _pageTable[l0][l1];
        }

        /// <summary>
        /// Sets a Page Table entry at a given GPU virtual address.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <param name="pte">Page table entry (CPU virtual address)</param>
        private void SetPte(ulong va, ulong pte)
        {
            ulong l0 = (va >> PtLvl0Bit) & PtLvl0Mask;
            ulong l1 = (va >> PtLvl1Bit) & PtLvl1Mask;

            if (_pageTable[l0] == null)
            {
                _pageTable[l0] = new ulong[PtLvl1Size];
                
                Array.Fill(_pageTable[l0], PteUnmapped);
            }

            _pageTable[l0][l1] = pte;
        }

        /// <summary>
        /// Creates a page table entry from a physical address and kind.
        /// </summary>
        /// <param name="pa">Physical address</param>
        /// <param name="kind">Kind</param>
        /// <returns>Page table entry</returns>
        private static ulong PackPte(ulong pa, PteKind kind)
        {
            return pa | ((ulong)kind << 56);
        }

        /// <summary>
        /// Unpacks kind from a page table entry.
        /// </summary>
        /// <param name="pte">Page table entry</param>
        /// <returns>Kind</returns>
        private static PteKind UnpackKindFromPte(ulong pte)
        {
            return (PteKind)(pte >> 56);
        }

        /// <summary>
        /// Unpacks physical address from a page table entry.
        /// </summary>
        /// <param name="pte">Page table entry</param>
        /// <returns>Physical address</returns>
        private static ulong UnpackPaFromPte(ulong pte)
        {
            return pte & 0xffffffffffffffUL;
        }
    }

    /// <summary>
    /// Registry of GPU accessible memory created through the Switch 2 nx2:gpu session.
    /// The GPU memory manager consults it when a GPU virtual address has no page table entry of
    /// its own, and routes reads/writes of sparse (GPU only) regions to their host backing.
    /// Shared by every memory manager of the emulation session; a title's GPU virtual addresses
    /// are unique, so a single registry is enough (one game at a time).
    /// </summary>
    public static class Nx2GpuMemory
    {
        /// <summary>
        /// Virtual size that sparse regions may reserve. Pages are committed by the host only
        /// when touched, so most of a large region costs address space, not RAM. Past the limit a
        /// region stays unbacked (reads zero, writes dropped) instead of failing the emulator.
        /// </summary>
        public const ulong SparseBudget = 64UL << 30;

        // Sparse regions reserve address space only; host memory is committed in chunks on the
        // first write, so a mostly empty region costs almost nothing.
        private const ulong SparseChunkSize = 1UL << 20;
        private const ulong SparseCommitBudget = 6UL << 30;

        /// <summary>
        /// A registered region of this size holds queue semaphores: page two is written by the
        /// GPU and mirrored to page one.
        /// </summary>
        public const ulong SemaphoreRegionSize = 0x2000;

        private sealed class MemoryEntry
        {
            public ulong Start;
            public ulong Size;
        }

        private sealed class MappingEntry
        {
            public ulong GpuStart;
            public ulong GpuSize;
            public ulong CpuStart;
        }

        private sealed class SparseEntry
        {
            public ulong Start;
            public ulong Size;
            public readonly Dictionary<ulong, MemoryBlock> Chunks = [];
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<uint, MemoryEntry> _memories = [];
        private static readonly List<MappingEntry> _mappings = [];
        private static readonly Dictionary<ulong, SparseEntry> _sparse = [];
        private static readonly List<ulong> _semaphores = [];

        // Snapshots for the lock free GPU side lookups. Rebuilt under _lock on every change.
        private static volatile MappingEntry[] _mappingSnapshot = [];
        private static volatile SparseEntry[] _sparseSnapshot = [];
        private static volatile ulong[] _semaphoreSnapshot = [];

        private static ulong _sparseBytes;
        private static ulong _sparseCommitted;
        private static ulong _sparseDropped;

        public static ulong SparseCommitted => _sparseCommitted;
        public static ulong SparseDropped => _sparseDropped;
        private static uint _nextHandle = 1;

        /// <summary>
        /// GPU virtual address of the queue semaphore regions registered so far.
        /// </summary>
        public static ReadOnlySpan<ulong> SemaphoreRegions => _semaphoreSnapshot;

        /// <summary>
        /// Registers a GPU accessible memory range (nx2:gpu 101).
        /// </summary>
        /// <param name="start">Start of the range (CPU and GPU address)</param>
        /// <param name="size">Size in bytes of the range</param>
        /// <returns>The memory handle, or 0 if the range is invalid</returns>
        public static uint Register(ulong start, ulong size)
        {
            if (size == 0 || start + size < start)
            {
                return 0;
            }

            lock (_lock)
            {
                uint handle = _nextHandle++;

                if (handle == 0)
                {
                    handle = _nextHandle++;
                }

                _memories[handle] = new MemoryEntry { Start = start, Size = size };

                if (size == SemaphoreRegionSize)
                {
                    AddSemaphoreLocked(start);
                }

                // Registering implies the mapping at its own address (identity), like the graphic
                // server does on the console.
                MapLocked(start, start, size);

                return handle;
            }
        }

        /// <summary>
        /// Destroys a registered memory range (nx2:gpu 102): its mappings and semaphore record go
        /// with it. The identity fallback can still resolve the guest range afterwards.
        /// </summary>
        /// <param name="handle">Handle returned by <see cref="Register"/></param>
        /// <returns>True if the handle was known</returns>
        public static bool Destroy(uint handle)
        {
            lock (_lock)
            {
                if (!_memories.Remove(handle, out MemoryEntry memory))
                {
                    return false;
                }

                UnmapLocked(memory.Start, memory.Size);

                if (memory.Size == SemaphoreRegionSize)
                {
                    RemoveSemaphoreLocked(memory.Start);
                }

                return true;
            }
        }

        /// <summary>
        /// Maps a registered memory range at a GPU virtual address (nx2:gpu 211).
        /// </summary>
        /// <param name="gpuStart">First GPU virtual address of the mapping</param>
        /// <param name="gpuSize">Size in bytes of the mapping</param>
        /// <param name="handle">Handle of the memory the mapping comes from</param>
        /// <param name="offset">Offset in bytes into the registered memory</param>
        /// <returns>True if the mapping was recorded</returns>
        public static bool Map(ulong gpuStart, ulong gpuSize, uint handle, ulong offset)
        {
            if (gpuSize == 0 || gpuStart + gpuSize < gpuStart || gpuSize > long.MaxValue)
            {
                return false;
            }

            lock (_lock)
            {
                if (!_memories.TryGetValue(handle, out MemoryEntry memory) || offset >= memory.Size)
                {
                    return false;
                }

                ulong available = memory.Size - offset;

                if (gpuSize > available)
                {
                    gpuSize = available;
                }

                MapLocked(gpuStart, memory.Start + offset, gpuSize);

                return true;
            }
        }

        /// <summary>
        /// Drops the mapping of a GPU virtual address range (nx2:gpu 213).
        /// </summary>
        /// <param name="gpuStart">First GPU virtual address to unmap</param>
        /// <param name="gpuSize">Size in bytes to unmap</param>
        public static void Unmap(ulong gpuStart, ulong gpuSize)
        {
            if (gpuSize == 0 || gpuStart + gpuSize < gpuStart)
            {
                return;
            }

            lock (_lock)
            {
                UnmapLocked(gpuStart, gpuSize);
            }
        }

        /// <summary>
        /// Creates a sparse (GPU only) region backed by host memory (nx2:gpu 214).
        /// A region already present at the same address is replaced.
        /// </summary>
        /// <param name="start">Start GPU virtual address, page aligned</param>
        /// <param name="size">Size in bytes, page aligned</param>
        /// <returns>True if the region was created</returns>
        public static bool CreateSparse(ulong start, ulong size)
        {
            return CreateRegion(start, size, false);
        }

        /// <summary>
        /// Creates a GPU-only region (nx2:gpu 214). Non-sparse regions are committed
        /// eagerly (the console backs them with real memory up front); sparse regions
        /// stay lazy and commit chunks as the GPU writes them.
        /// </summary>
        public static bool CreateRegion(ulong start, ulong size, bool eager)
        {
            if (size == 0 || start + size < start ||
                (start & MemoryManager.PageMask) != 0 || (size & MemoryManager.PageMask) != 0)
            {
                return false;
            }

            lock (_lock)
            {
                if (_sparse.TryGetValue(start, out SparseEntry existing))
                {
                    DestroySparseLocked(existing);
                }

                foreach (SparseEntry region in _sparse.Values)
                {
                    if (start < region.Start + region.Size && region.Start < start + size)
                    {
                        return false;
                    }
                }

                if (size <= SparseBudget - _sparseBytes)
                {
                    _sparseBytes += size;
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu,
                        $"NX2 sparse region 0x{start:X} (0x{size:X} bytes) exceeds the virtual budget, writes dropped");
                }

                SparseEntry entry = new() { Start = start, Size = size };

                if (eager)
                {
                    ulong chunks = (size + SparseChunkSize - 1) / SparseChunkSize;

                    for (ulong i = 0; i < chunks; i++)
                    {
                        if (SparseChunkSize > SparseCommitBudget - _sparseCommitted)
                        {
                            Logger.Warning?.Print(LogClass.Gpu,
                                $"NX2 eager region 0x{start:X} hit the {SparseCommitBudget >> 30} GiB commit budget at chunk {i}");

                            break;
                        }

                        entry.Chunks[i] = new MemoryBlock((long)SparseChunkSize);
                        _sparseCommitted += SparseChunkSize;
                    }
                }

                _sparse[start] = entry;
                RebuildSparseLocked();

                return true;
            }
        }

        /// <summary>
        /// Destroys a sparse region (nx2:gpu 215) and frees its host backing.
        /// </summary>
        /// <param name="start">Start GPU virtual address</param>
        /// <returns>True if a sparse region started there</returns>
        public static bool DestroySparse(ulong start)
        {
            lock (_lock)
            {
                if (!_sparse.TryGetValue(start, out SparseEntry region))
                {
                    return false;
                }

                DestroySparseLocked(region);
                RebuildSparseLocked();

                return true;
            }
        }

        /// <summary>
        /// Checks if a GPU virtual address lies in a sparse region.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <returns>True if the address is sparse</returns>
        public static bool IsSparse(ulong va)
        {
            SparseEntry[] entries = _sparseSnapshot;

            if (entries.Length == 0)
            {
                return false;
            }

            int index = FindStart(entries, va);

            return index >= 0 && va - entries[index].Start < entries[index].Size;
        }

        /// <summary>
        /// Resolves a GPU virtual address through the recorded nx2:gpu mappings.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <param name="cpuAddress">CPU address the page is mapped to</param>
        /// <returns>True if a mapping covers the address</returns>
        public static bool TryResolveMapping(ulong va, out ulong cpuAddress)
        {
            MappingEntry[] entries = _mappingSnapshot;

            int index = entries.Length == 0 ? -1 : FindStart(entries, va);

            if (index >= 0 && va - entries[index].GpuStart < entries[index].GpuSize)
            {
                cpuAddress = entries[index].CpuStart + (va - entries[index].GpuStart);

                return true;
            }

            cpuAddress = 0;

            return false;
        }

        /// <summary>
        /// Reads a value from a sparse region. Addresses outside every region are not handled.
        /// </summary>
        /// <typeparam name="T">Type of the value</typeparam>
        /// <param name="va">GPU virtual address</param>
        /// <param name="value">Value read, or zero when the region has no backing</param>
        /// <returns>True if the address is in a sparse region</returns>
        public static unsafe bool TryReadSparse<T>(ulong va, out T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();

            lock (_lock)
            {
                SparseEntry region = FindSparseLocked(va, (ulong)size);

                if (region == null)
                {
                    value = default;

                    return false;
                }

                ulong offset = va - region.Start;
                ulong inChunk = offset % SparseChunkSize;
                MemoryBlock chunk = GetChunkLocked(region, offset, false);

                value = chunk == null
                    ? default
                    : Unsafe.ReadUnaligned<T>((byte*)chunk.Pointer + inChunk);

                return true;
            }
        }

        /// <summary>
        /// Reads from a sparse region into a span. Bytes past the end of the region read as zero.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <param name="data">Destination span</param>
        /// <returns>True if the address is in a sparse region</returns>
        public static unsafe bool TryReadSparse(ulong va, Span<byte> data)
        {
            lock (_lock)
            {
                SparseEntry region = FindSparseLocked(va, (ulong)data.Length);

                if (region == null)
                {
                    return false;
                }

                ulong available = region.Size - (va - region.Start);
                int size = (int)Math.Min((ulong)data.Length, available);

                CopyFromChunks(region, va - region.Start, data[..size]);

                data[size..].Clear();

                return true;
            }
        }

        /// <summary>
        /// Writes into a sparse region. Bytes past the end of the region are dropped.
        /// </summary>
        /// <param name="va">GPU virtual address</param>
        /// <param name="data">Data to write</param>
        /// <returns>True if the address is in a sparse region</returns>
        public static unsafe bool TryWriteSparse(ulong va, ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                SparseEntry region = FindSparseLocked(va, (ulong)data.Length);

                if (region == null)
                {
                    return false;
                }

                ulong available = region.Size - (va - region.Start);
                int size = (int)Math.Min((ulong)data.Length, available);

                CopyToChunks(region, va - region.Start, data[..size]);

                return true;
            }
        }

        private static void MapLocked(ulong gpuStart, ulong cpuStart, ulong size)
        {
            TrimMappingsLocked(gpuStart, size);

            _mappings.Add(new MappingEntry { GpuStart = gpuStart, CpuStart = cpuStart, GpuSize = size });
            RebuildMappingsLocked();
        }

        private static void UnmapLocked(ulong gpuStart, ulong size)
        {
            TrimMappingsLocked(gpuStart, size);
            RebuildMappingsLocked();
        }

        // Drops [gpuStart, +size) from the recorded mappings, keeping the parts outside it.
        private static void TrimMappingsLocked(ulong gpuStart, ulong size)
        {
            ulong end = gpuStart + size;
            List<MappingEntry> kept = new(_mappings.Count + 1);

            foreach (MappingEntry mapping in _mappings)
            {
                ulong mappingEnd = mapping.GpuStart + mapping.GpuSize;

                if (mappingEnd <= gpuStart || mapping.GpuStart >= end)
                {
                    kept.Add(mapping);
                    continue;
                }

                if (mapping.GpuStart < gpuStart)
                {
                    kept.Add(new MappingEntry
                    {
                        GpuStart = mapping.GpuStart,
                        CpuStart = mapping.CpuStart,
                        GpuSize = gpuStart - mapping.GpuStart,
                    });
                }

                if (mappingEnd > end)
                {
                    kept.Add(new MappingEntry
                    {
                        GpuStart = end,
                        CpuStart = mapping.CpuStart + (end - mapping.GpuStart),
                        GpuSize = mappingEnd - end,
                    });
                }
            }

            _mappings.Clear();
            _mappings.AddRange(kept);
        }

        private static void RebuildMappingsLocked()
        {
            _mappings.Sort((a, b) => a.GpuStart.CompareTo(b.GpuStart));
            _mappingSnapshot = _mappings.ToArray();
        }

        private static void DestroySparseLocked(SparseEntry region)
        {
            _sparse.Remove(region.Start);

            _sparseBytes -= region.Size;

            foreach (MemoryBlock chunk in region.Chunks.Values)
            {
                _sparseCommitted -= SparseChunkSize;
                chunk.Dispose();
            }

            region.Chunks.Clear();
        }

        private static void RebuildSparseLocked()
        {
            SparseEntry[] entries = new SparseEntry[_sparse.Count];
            _sparse.Values.CopyTo(entries, 0);
            Array.Sort(entries, (a, b) => a.Start.CompareTo(b.Start));
            _sparseSnapshot = entries;
        }

        private static SparseEntry FindSparseLocked(ulong va, ulong size)
        {
            SparseEntry[] entries = _sparseSnapshot;
            int index = entries.Length == 0 ? -1 : FindStart(entries, va);

            if (index < 0)
            {
                return null;
            }

            SparseEntry region = entries[index];

            if (va - region.Start > region.Size || size > region.Size - (va - region.Start))
            {
                return null;
            }

            return region;
        }

        private static MemoryBlock GetChunkLocked(SparseEntry region, ulong offset, bool create)
        {
            ulong index = offset / SparseChunkSize;

            if (region.Chunks.TryGetValue(index, out MemoryBlock chunk))
            {
                return chunk;
            }

            if (!create || SparseChunkSize > SparseCommitBudget - _sparseCommitted)
            {
                return null;
            }

            chunk = new MemoryBlock((long)SparseChunkSize);

            region.Chunks[index] = chunk;
            _sparseCommitted += SparseChunkSize;

            return chunk;
        }

        private static unsafe void CopyFromChunks(SparseEntry region, ulong offset, Span<byte> data)
        {
            int done = 0;

            while (done < data.Length)
            {
                ulong position = offset + (ulong)done;
                ulong inChunk = position % SparseChunkSize;
                int take = (int)Math.Min((ulong)(data.Length - done), SparseChunkSize - inChunk);
                MemoryBlock chunk = GetChunkLocked(region, position, false);

                if (chunk == null)
                {
                    data.Slice(done, take).Clear();
                }
                else
                {
                    new ReadOnlySpan<byte>((byte*)chunk.Pointer + inChunk, take).CopyTo(data.Slice(done, take));
                }

                done += take;
            }
        }

        private static unsafe void CopyToChunks(SparseEntry region, ulong offset, ReadOnlySpan<byte> data)
        {
            int done = 0;

            while (done < data.Length)
            {
                ulong position = offset + (ulong)done;
                ulong inChunk = position % SparseChunkSize;
                int take = (int)Math.Min((ulong)(data.Length - done), SparseChunkSize - inChunk);
                MemoryBlock chunk = GetChunkLocked(region, position, true);

                if (chunk != null)
                {
                    data.Slice(done, take).CopyTo(new Span<byte>((byte*)chunk.Pointer + inChunk, take));
                }
                else
                {
                    System.Threading.Interlocked.Add(ref _sparseDropped, (ulong)take);
                }

                done += take;
            }
        }

        private static void AddSemaphoreLocked(ulong start)
        {
            if (!_semaphores.Contains(start))
            {
                _semaphores.Add(start);
                _semaphoreSnapshot = _semaphores.ToArray();
            }
        }

        private static void RemoveSemaphoreLocked(ulong start)
        {
            if (_semaphores.Remove(start))
            {
                _semaphoreSnapshot = _semaphores.ToArray();
            }
        }

        // Index of the last entry with Start <= address, or -1 when there is none.
        private static int FindStart(SparseEntry[] entries, ulong address)
        {
            int low = 0;
            int high = entries.Length - 1;
            int found = -1;

            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);

                if (entries[middle].Start <= address)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        // Index of the last entry with GpuStart <= address, or -1 when there is none.
        private static int FindStart(MappingEntry[] entries, ulong address)
        {
            int low = 0;
            int high = entries.Length - 1;
            int found = -1;

            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);

                if (entries[middle].GpuStart <= address)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }
    }
}
