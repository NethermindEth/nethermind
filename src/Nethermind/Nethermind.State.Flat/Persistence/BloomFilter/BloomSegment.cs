// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using DotNext.IO.MemoryMappedFiles;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class BloomSegment : IDisposable
{
    // ---- layout / constants ----
    private const int CacheLineBytes = 64;      // 512 bits
    private const int CacheLineBits  = 512;
    private const int HeaderSize     = 128;
    private const int PageSize       = 4096;    // Standard OS page size
    private const int ShiftPerPage   = 12;      // 2^12 = 4096
    private const ulong Magic        = 0x424C4F4F4DUL; // "BLOOM"

    // golden ratio for probe dispersion
    private const ulong ProbeDelta = 0x9E3779B97F4A7C15UL;

    // ---- persisted metadata ----
    public long Capacity { get; private set; }
    public int BitsPerKey { get; private set; }
    public int K { get; private set; }

    private long _count;
    public long Count => Volatile.Read(ref _count);

    private int _sealed; // 0/1
    public bool IsSealed => Volatile.Read(ref _sealed) != 0;

    // ---- layout ----
    private readonly long _dataOffset;
    private readonly long _numBlocks;
    private readonly long _totalMappedSize;

    // ---- memory and persistence ----
    private readonly string _path;
    private readonly SafeFileHandle _fileHandle;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedDirectAccessor _accessor;

    // ---- manual dirty tracking ----
    private readonly ulong[] _dirtyPages;

    // ---- concurrency barrier ----
    private int _writersInFlight;
    private int _stopWriters;
    private readonly Lock _maintenance = new();
    private int _opsInFlight;
    private int _isDisposingOrDisposed; // 0 active, 1 disposing/disposed

    private long _addedSinceLastFlush;

    private BloomSegment(
        string path,
        SafeFileHandle fileHandle,
        long capacity,
        int bitsPerKey,
        long count,
        bool isSealed)
    {
        _path = path;
        _fileHandle = fileHandle;
        Capacity = capacity;
        BitsPerKey = bitsPerKey;
        _count = count;
        _sealed = isSealed ? 1 : 0;
        _stopWriters = isSealed ? 1 : 0;

        K = Math.Max(1, (int)Math.Round(bitsPerKey * Math.Log(2)));

        long totalBytes;
        checked
        {
            totalBytes = AlignUp((capacity * bitsPerKey + 7) / 8, CacheLineBytes);
        }

        _numBlocks = totalBytes / CacheLineBytes;
        _dataOffset = AlignUp(HeaderSize, CacheLineBytes);
        _totalMappedSize = _dataOffset + totalBytes;

        // Dirty tracking bitset
        long pageCount = (_totalMappedSize + PageSize - 1) / PageSize;
        _dirtyPages = new ulong[(pageCount + 63) / 64];

        // Anonymous MMF backing memory
        _mmf = MemoryMappedFile.CreateNew(null, _totalMappedSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateDirectAccessor(0, _totalMappedSize, MemoryMappedFileAccess.ReadWrite);

        ApplyLinuxOptimizations((IntPtr)_accessor.Pointer.Address, _totalMappedSize);
    }

    public static BloomSegment CreateNew(string path, long capacity, int bitsPerKey)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        var segment = new BloomSegment(path, handle, capacity, bitsPerKey, 0, false);

        // header is in mapped memory; mark page 0 dirty so it gets persisted by Flush
        segment.WriteHeaderToMappedMemory();
        segment.MarkPageDirty(0);
        return segment;
    }

    public static unsafe BloomSegment OpenExisting(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Read header first to discover parameters
        byte* headerBuffer = stackalloc byte[HeaderSize];
        RandomAccess.Read(handle, new Span<byte>(headerBuffer, HeaderSize), 0);

        if (Unsafe.ReadUnaligned<ulong>(ref headerBuffer[0]) != Magic)
            throw new InvalidDataException("Invalid bloom segment header");

        long capacity = Unsafe.ReadUnaligned<long>(ref headerBuffer[8]);
        int bitsPerKey = Unsafe.ReadUnaligned<int>(ref headerBuffer[16]);
        long count = Unsafe.ReadUnaligned<long>(ref headerBuffer[24]);
        bool isSealed = Unsafe.ReadUnaligned<int>(ref headerBuffer[32]) != 0;

        var segment = new BloomSegment(path, handle, capacity, bitsPerKey, count, isSealed);

        // Load file into anonymous mapping (chunked; safe for >2GB)
        segment.LoadFromFile();
        return segment;
    }

    public bool IsFull => Count >= Capacity;
    public string Path => _path;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkPageDirty(long offset)
    {
        long pageIdx = offset >> ShiftPerPage;
        int wordIdx = (int)(pageIdx >> 6);
        ulong mask = 1UL << (int)(pageIdx & 63);

        ref long word = ref Unsafe.As<ulong, long>(ref _dirtyPages[wordIdx]);
        Interlocked.Or(ref word, (long)mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe bool MightContain(ulong h1)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(BloomSegment));

        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);

        // Base pointer once
        ulong* lanes = (ulong*)((byte*)_accessor.Pointer.Address + baseOffset);

        // Precompute probe sequence once
        int start = (int)(h2 & (CacheLineBits - 1));
        int step  = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);

        int k = K;
        for (int i = 0; i < k; i++)
        {
            int bit = (start + i * step) & (CacheLineBits - 1);
            int laneIndex = bit >> 6;
            ulong mask = 1UL << (bit & 63);

            if ((lanes[laneIndex] & mask) == 0)
                return false;
        }

        return true;
    }

    public unsafe bool TryAdd(ulong h1)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            return false;

        if (Volatile.Read(ref _sealed) != 0)
            return false;

        Interlocked.Increment(ref _opsInFlight);
        try
        {
            while (true)
            {
                Interlocked.Increment(ref _writersInFlight);

                if (Volatile.Read(ref _stopWriters) == 0 &&
                    Volatile.Read(ref _sealed) == 0 &&
                    Volatile.Read(ref _isDisposingOrDisposed) == 0)
                {
                    break;
                }

                Interlocked.Decrement(ref _writersInFlight);

                // Permanent barriers: fail fast
                if (Volatile.Read(ref _sealed) != 0 || Volatile.Read(ref _isDisposingOrDisposed) != 0)
                    return false;

                return false;
            }

            try
            {
                GetBlockAndHashes(h1, out ulong h2, out long baseOffset);
                byte* blockPtr = (byte*)_accessor.Pointer.Address + baseOffset;

                int start = (int)(h2 & (CacheLineBits - 1));
                int step  = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);

                int k = K;
                for (int i = 0; i < k; i++)
                {
                    int bit = (start + i * step) & (CacheLineBits - 1);
                    ref long lane = ref *(long*)(blockPtr + ((bit >> 6) * 8));
                    Interlocked.Or(ref lane, (long)(1UL << (bit & 63)));
                }

                MarkPageDirty(baseOffset);
                Interlocked.Increment(ref _count);
                Interlocked.Increment(ref _addedSinceLastFlush);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _writersInFlight);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _opsInFlight);
        }
    }

    /// <summary>
    /// Persist dirty pages to the segment file. Safe for mappings larger than 2GB.
    /// </summary>
    public void Flush()
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(BloomSegment));

        using var _ = _maintenance.EnterScope();
        DoFlush_NoLock();
    }

    private unsafe void DoFlush_NoLock()
    {
        // Update in-memory header then mark page 0 dirty so it is persisted.
        WriteHeaderToMappedMemory();
        MarkPageDirty(0);

        long added = Interlocked.Exchange(ref _addedSinceLastFlush, 0);

        for (int i = 0; i < _dirtyPages.Length; i++)
        {
            ulong word = (ulong)Interlocked.Exchange(ref Unsafe.As<ulong, long>(ref _dirtyPages[i]), 0);
            if (word == 0) continue;

            while (word != 0)
            {
                int b = BitOperations.TrailingZeroCount(word);
                long pageIdx = (long)i * 64 + b;
                long offset = pageIdx * PageSize;

                // Clamp to mapped size
                long remaining = _totalMappedSize - offset;
                if (remaining <= 0)
                {
                    word &= (word - 1);
                    continue;
                }

                int sizeToWrite = (int)Math.Min(PageSize, remaining); // <= 4096

                ReadOnlySpan<byte> data = new((byte*)_accessor.Pointer.Address + offset, sizeToWrite);
                RandomAccess.Write(_fileHandle, data, offset);

                word &= (word - 1);
            }
        }

        // Make durable
        RandomAccess.FlushToDisk(_fileHandle);

        _ = added; // keep for future logging/metrics if desired
    }

    public void Seal()
    {
        using var _ = _maintenance.EnterScope();
        Volatile.Write(ref _stopWriters, 1);
        Volatile.Write(ref _sealed, 1);

        WaitForWritersToDrain();
        DoFlush_NoLock(); // don't re-enter _maintenance
    }

    private unsafe void LoadFromFile()
    {
        long fileSize = RandomAccess.GetLength(_fileHandle);
        long total = Math.Min(fileSize, _totalMappedSize);

        byte* basePtr = (byte*)_accessor.Pointer.Address;

        // Read directly into the mapping in reasonably sized chunks.
        // Important: Span<byte> length must be int, so we chunk <= int.MaxValue (and much smaller in practice).
        const int ChunkSize = 4 * 1024 * 1024; // 4 MiB

        long offset = 0;
        while (offset < total)
        {
            int chunk = (int)Math.Min(ChunkSize, total - offset);
            Span<byte> dest = new(basePtr + offset, chunk);
            int read = RandomAccess.Read(_fileHandle, dest, offset);
            if (read <= 0) break;

            // If short read, advance by actual bytes read.
            offset += read;
        }
    }

    /// <summary>
    /// Writes the 128B header into mapped memory using only tiny spans (safe for >2GB mappings).
    /// Avoids using _accessor.Bytes which cannot represent spans > 2GB.
    /// </summary>
    private unsafe void WriteHeaderToMappedMemory()
    {
        byte* p = (byte*)_accessor.Pointer.Address;

        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(p + 0, 8), Magic);
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(p + 8, 8), Capacity);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(p + 16, 4), BitsPerKey);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(p + 20, 4), K);
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(p + 24, 8), Count);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(p + 32, 4), IsSealed ? 1 : 0);

        // Remaining header bytes are reserved; no need to touch.
    }

    private static void ApplyLinuxOptimizations(IntPtr address, long length)
    {
        if (OperatingSystem.IsLinux())
        {
            madvise(address, (UIntPtr)length, 1);  // MADV_RANDOM
            madvise(address, (UIntPtr)length, 14); // MADV_HUGEPAGE
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int madvise(IntPtr addr, UIntPtr length, int advice);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetBlockAndHashes(ulong h1, out ulong h2, out long baseOffset)
    {
        h2 = Mix64(h1);
        ulong hb = Mix64(h1 ^ ProbeDelta);
        long block = (long)(hb % (ulong)_numBlocks);
        baseOffset = _dataOffset + block * CacheLineBytes;
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

    private void WaitForWritersToDrain()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _writersInFlight) != 0) sw.SpinOnce();
    }

    private void WaitForOpsToDrain()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _opsInFlight) != 0) sw.SpinOnce();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposingOrDisposed, 1) != 0) return;
        Volatile.Write(ref _stopWriters, 1);

        WaitForWritersToDrain();
        WaitForOpsToDrain();

        try
        {
            using var _ = _maintenance.EnterScope();
            DoFlush_NoLock(); // don't call Flush() here (would throw / re-enter)
        }
        catch
        {
            // Best-effort on dispose: swallow.
        }

        _accessor.Dispose();
        _mmf.Dispose();
        _fileHandle.Dispose();
    }
}
