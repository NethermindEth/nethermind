// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

/// <summary>
/// Pure in-memory cache-local (64B / 512-bit) Bloom filter.
/// Owns its memory and supports atomic adds + AVX2-optimized queries.
/// Optimized for Linux Transparent Huge Pages (THP).
/// AI-generated based on RocksDB's bloom filter implementation.
/// </summary>
public sealed unsafe class BloomFilter : IDisposable
{
    // ---- constants ----
    private const int CacheLineBytes = 64;      // 512 bits

    // RocksDB golden ratio constants
    private const uint Mul32 = 0x9E3779B9u;
    private const uint Mul8 = 0xAB25F4C1u;

    // Linux THP constants
    private const int MADV_HUGEPAGE = 14;
    private const nuint HugePageSize = 2 * 1024 * 1024; // 2MB

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int Madvise(void* addr, nuint length, int advice);

    public long Capacity { get; }
    public double BitsPerKey { get; }
    public int K { get; }

    public long Count => Volatile.Read(ref _count);

    // Total bloom data bytes (no header), always multiple of 64 bytes
    public long DataBytes { get; }
    public long NumBlocks { get; } // number of 64B cache lines

    private long _count;

    // 64-byte aligned base address for AVX loads (and cacheline semantics)
    private byte* _data;
    private nuint _dataSize;
    private int _disposed;

    public BloomFilter(long capacity, double bitsPerKey, long initialCount = 0)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (bitsPerKey <= 0.0 || double.IsNaN(bitsPerKey) || double.IsInfinity(bitsPerKey))
            throw new ArgumentOutOfRangeException(nameof(bitsPerKey), "BitsPerKey must be a finite value > 0.");

        Capacity = capacity;
        BitsPerKey = bitsPerKey;
        _count = initialCount;

        K = ChooseNumProbesRocks(bitsPerKey);

        long totalBytes = AlignUp((long)Math.Ceiling(capacity * bitsPerKey / 8.0), CacheLineBytes);
        DataBytes = totalBytes;
        NumBlocks = totalBytes / CacheLineBytes;

        _dataSize = checked((nuint)totalBytes);

        // Determine alignment:
        // On Linux, if the size is large enough (>2MB), we align to 2MB boundaries.
        // This allows the OS to back the allocation with Transparent Huge Pages (THP),
        // significantly reducing TLB misses for large Bloom Filters.
        nuint alignment = CacheLineBytes;
        bool useHugePages = false;

        if (OperatingSystem.IsLinux() && _dataSize >= HugePageSize)
        {
            alignment = HugePageSize;
            useHugePages = true;
        }

        _data = (byte*)NativeMemory.AlignedAlloc(_dataSize, alignment);
        if (_data == null) throw new OutOfMemoryException();

        // Hint the kernel to use huge pages BEFORE we touch the memory (Clear).
        // This ensures that when Clear() triggers page faults, the kernel allocates 2MB physical pages immediately.
        if (useHugePages)
        {
            Madvise(_data, _dataSize, MADV_HUGEPAGE);
        }

        // zero init
        // Note: For huge allocations, this loop will trigger the actual physical memory allocation.
        new Span<byte>(_data, checked((int)Math.Min(totalBytes, int.MaxValue))).Clear();
        if (totalBytes > int.MaxValue)
        {
            // chunk clear for huge allocations
            long off = 0;
            const int Chunk = 8 * 1024 * 1024;
            while (off < totalBytes)
            {
                int len = (int)Math.Min(Chunk, totalBytes - off);
                new Span<byte>(_data + off, len).Clear();
                off += len;
            }
        }
    }

    /// <summary>
    /// Returns the 64B cacheline byte offset within the bloom data that was touched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Add(ulong key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(BloomFilter));

        GetLineAndHashState(key, NumBlocks, out long lineIndex, out uint h);

        byte* linePtr = _data + lineIndex * CacheLineBytes;

        // Scalar atomic add (SIMD add isn't worth it with atomics)
        const int shift = 32 - 9; // log2(512)=9
        int k = K;
        for (int i = 0; i < k; i++)
        {
            int bit = (int)(h >> shift);
            ref long lane = ref *(long*)(linePtr + ((bit >> 6) * 8));
            Interlocked.Or(ref lane, (long)(1UL << (bit & 63)));
            h *= Mul32;
        }

        Interlocked.Increment(ref _count);
        return lineIndex * CacheLineBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MightContain(ulong key)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(BloomFilter));

        GetLineAndHashState(key, NumBlocks, out long lineIndex, out uint h);

        byte* linePtr = _data + lineIndex * CacheLineBytes;

        if (Avx2.IsSupported)
        {
            return HashMayMatchPreparedAvx2(h, K, linePtr);
        }

        // Scalar fallback
        ulong* lanes = (ulong*)linePtr;
        const int shift = 32 - 9;

        int k = K;
        for (int i = 0; i < k; i++)
        {
            int bit = (int)(h >> shift);
            int laneIndex = bit >> 6;
            ulong mask = 1UL << (bit & 63);
            if ((lanes[laneIndex] & mask) == 0) return false;
            h *= Mul32;
        }

        return true;
    }

    /// <summary>Zero the bloom bits and reset count to 0.</summary>
    public void Clear()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(BloomFilter));

        long totalBytes = DataBytes;
        long off = 0;
        const int Chunk = 8 * 1024 * 1024;
        while (off < totalBytes)
        {
            int len = (int)Math.Min(Chunk, totalBytes - off);
            new Span<byte>(_data + off, len).Clear();
            off += len;
        }
        Volatile.Write(ref _count, 0);
    }

    internal byte* DangerousGetDataPointer() => _data;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_data != null)
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
            _dataSize = 0;
        }
    }

    // ----------------- internal helpers -----------------

    private static int ChooseNumProbesRocks(double bitsPerKey)
    {
        int mbpk = (int)Math.Round(bitsPerKey * 1000.0);

        return mbpk switch
        {
            <= 2080 => 1,
            <= 3580 => 2,
            <= 5100 => 3,
            <= 6640 => 4,
            <= 8300 => 5,
            <= 10070 => 6,
            <= 11720 => 7,
            <= 14001 => 8,
            <= 16050 => 9,
            <= 18300 => 10,
            <= 22001 => 11,
            <= 25501 => 12,
            > 50000 => 24,
            _ => (mbpk - 1) / 2000 - 1
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetLineAndHashState(ulong key, long numBlocks, out long lineIndex, out uint h)
    {
        // One 64-bit mix, split into two 32-bit values like RocksDB
        ulong x = Mix64(key);
        uint h1 = (uint)x;
        uint h2 = (uint)(x >> 32);

        lineIndex = (long)((ulong)numBlocks <= uint.MaxValue
            ? ((ulong)h1 * (ulong)(uint)numBlocks) >> 32 // FastRange32-style: floor(h1 * numBlocks / 2^32)
            : (ulong)(((UInt128)x * (ulong)numBlocks) >> 64)); // 64-bit multiply-high: floor(x * numBlocks / 2^64)

        h = h2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HashMayMatchPreparedAvx2(uint h2, int numProbes, byte* dataAtCacheLine)
    {
        // Powers of 32-bit golden ratio, mod 2^32 (same as RocksDB)
        Vector256<uint> multipliers = Vector256.Create(
            0x00000001u, 0x9E3779B9u, 0xE35E67B1u, 0x734297E9u,
            0x35FBE861u, 0xDEB7C719u, 0x0448B211u, 0x3459B749u
        );

        int rem = numProbes;
        uint h = h2;

        // Two 256-bit loads = 64 bytes = 512 bits (aligned allocation, so aligned loads ok)
        Vector256<uint> lower = Avx.LoadVector256((uint*)dataAtCacheLine);
        Vector256<uint> upper = Avx.LoadVector256((uint*)(dataAtCacheLine + 32));

        Vector256<int> zeroToSeven = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<uint> ones = Vector256.Create(1u);

        for (; ; )
        {
            Vector256<uint> hashVec = Vector256.Create(h);
            hashVec = Avx2.MultiplyLow(hashVec, multipliers);

            // top 4 bits -> word address 0..15
            Vector256<uint> wordAddr = Avx2.ShiftRightLogical(hashVec, 28);

            Vector256<uint> pLower = Avx2.PermuteVar8x32(lower, wordAddr);
            Vector256<uint> pUpper = Avx2.PermuteVar8x32(upper, wordAddr);

            // Select upper vs lower based on sign bit (equivalent to top bit of word address)
            Vector256<int> upperLowerSelector = Avx2.ShiftRightArithmetic(hashVec.AsInt32(), 31);
            Vector256<byte> selectedBytes =
                Avx2.BlendVariable(pLower.AsByte(), pUpper.AsByte(), upperLowerSelector.AsByte());
            Vector256<uint> valueVec = selectedBytes.AsUInt32();

            // lanes 0..(rem-1) active
            Vector256<int> remV = Vector256.Create(rem);
            Vector256<int> kSel = Avx2.CompareGreaterThan(remV, zeroToSeven);

            // bit-within-word: (hashVec << 4) >> 27  => 0..31
            Vector256<uint> bitAddr = Avx2.ShiftLeftLogical(hashVec, 4);
            bitAddr = Avx2.ShiftRightLogical(bitAddr, 27);

            Vector256<uint> bitMask = Avx2.ShiftLeftLogicalVariable(ones, bitAddr);
            bitMask = Avx2.And(bitMask, kSel.AsUInt32());

            bool match = Avx2.TestC(valueVec.AsByte(), bitMask.AsByte());

            if (rem <= 8) return match;
            if (!match) return false;

            h *= Mul8;
            rem -= 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix64(ulong x)
    {
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
