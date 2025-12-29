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
using Autofac.Features.ResolveAnything;
using Microsoft.Win32.SafeHandles;
using DotNext.IO.MemoryMappedFiles;
using Org.BouncyCastle.Bcpg.OpenPgp;

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
    private int _isDisposingOrDisposed;
    private bool _disposed;

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

        // 1. Initialize Dirty Tracking Bitset
        long pageCount = (_totalMappedSize + PageSize - 1) / PageSize;
        _dirtyPages = new ulong[(pageCount + 63) / 64];

        // 2. Create Anonymous MMF (Backing memory only)
        _mmf = MemoryMappedFile.CreateNew(null, _totalMappedSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateDirectAccessor(0, _totalMappedSize, MemoryMappedFileAccess.ReadWrite);

        ApplyLinuxOptimizations((IntPtr)_accessor.Pointer.Address, _totalMappedSize);
    }

    public static BloomSegment CreateNew(string path, long capacity, int bitsPerKey)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        var segment = new BloomSegment(path, handle, capacity, bitsPerKey, 0, false);

        segment.WriteHeader();
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

        // Load the rest of the file into the anonymous memory
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

    public unsafe bool MightContain(ulong h1)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(BloomSegment));

        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);
        byte* blockPtr = (byte*)_accessor.Pointer.Address + baseOffset;

        ulong* l = (ulong*)blockPtr;

        for (int i = 0; i < K; i++)
        {
            GetLaneInBlock(h2, i, out int laneIndex, out ulong mask);
            if ((l[laneIndex] & mask) == 0)
                return false;
        }
        return true;
    }

    public unsafe bool TryAdd(ulong h1)
    {
        // "Try" semantics: no exceptions for normal terminal states
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

                // Fast path: writers allowed + not sealed/disposed
                if (Volatile.Read(ref _stopWriters) == 0 &&
                    Volatile.Read(ref _sealed) == 0 &&
                    Volatile.Read(ref _isDisposingOrDisposed) == 0)
                {
                    break;
                }

                Interlocked.Decrement(ref _writersInFlight);

                // If we're sealed/disposed, do NOT spin forever: just fail.
                if (Volatile.Read(ref _sealed) != 0 || Volatile.Read(ref _isDisposingOrDisposed) != 0)
                    return false;

                // If stopWriters is used as a temporary barrier in the future, you can wait here.
                // With current code, stopWriters becomes permanent for Seal/Dispose, so fail fast.
                return false;
            }

            try
            {
                GetBlockAndHashes(h1, out ulong h2, out long baseOffset);
                byte* blockPtr = (byte*)_accessor.Pointer.Address + baseOffset;

                int start = (int)(h2 & (CacheLineBits - 1));
                int step  = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);

                for (int i = 0; i < K; i++)
                {
                    int bit = (start + i * step) & (CacheLineBits - 1);
                    ref long lane = ref *(long*)(blockPtr + ((bit >> 6) * 8));
                    Interlocked.Or(ref lane, (long)(1UL << (bit & 63)));
                }

                MarkPageDirty(baseOffset);
                Interlocked.Increment(ref _count);
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

    public void Flush()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BloomSegment));

        using var _ = _maintenance.EnterScope();

        WriteHeader();
        MarkPageDirty(0);

        for (int i = 0; i < _dirtyPages.Length; i++)
        {
            ulong word = (ulong)Interlocked.Exchange(ref Unsafe.As<ulong, long>(ref _dirtyPages[i]), 0);
            if (word == 0) continue;

            while (word != 0)
            {
                int b = BitOperations.TrailingZeroCount(word);
                long pageIdx = (long)i * 64 + b;
                long offset = pageIdx * PageSize;
                long sizeToWrite = Math.Min(PageSize, _totalMappedSize - offset);

                if (sizeToWrite > 0)
                {
                    unsafe
                    {
                        ReadOnlySpan<byte> data = new((byte*)_accessor.Pointer.Address + offset, (int)sizeToWrite);
                        RandomAccess.Write(_fileHandle, data, offset);
                    }
                }

                word &= (word - 1);
            }
        }

        RandomAccess.FlushToDisk(_fileHandle);
    }

    public void Seal()
    {
        using var _ = _maintenance.EnterScope();
        Volatile.Write(ref _stopWriters, 1);
        Volatile.Write(ref _sealed, 1);

        WaitForWritersToDrain();
        Flush();
    }

    private unsafe void LoadFromFile()
    {
        long fileSize = RandomAccess.GetLength(_fileHandle);
        long toRead = Math.Min(fileSize, _totalMappedSize);
        Span<byte> dest = new((byte*)_accessor.Pointer.Address, (int)toRead);
        RandomAccess.Read(_fileHandle, dest, 0);
    }

    /*
    private unsafe void WriteHeader()
    {
        byte* p = (byte*)_accessor.Pointer.Address;
        Unsafe.WriteUnaligned(ref p[0], Magic);
        Unsafe.WriteUnaligned(ref p[8], Capacity);
        Unsafe.WriteUnaligned(ref p[16], BitsPerKey);
        Unsafe.WriteUnaligned(ref p[20], K);
        Unsafe.WriteUnaligned(ref p[24], Count);
        Unsafe.WriteUnaligned(ref p[32], IsSealed ? 1 : 0);
    }
    */

    private void WriteHeader()
    {
        Span<byte> bytes = _accessor.Bytes;

        if (bytes.Length < HeaderSize)
            throw new InvalidOperationException(
                $"BloomSegment header write out of range. Path='{_path}', BytesLength={bytes.Length}, HeaderSize={HeaderSize}");

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(0, 8), Magic);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(8, 8), Capacity);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(16, 4), BitsPerKey);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(20, 4), K);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(24, 8), Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(32, 4), IsSealed ? 1 : 0);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetLaneInBlock(ulong h2, int probeIndex, out int laneIndex, out ulong mask)
    {
        int start = (int)(h2 & (CacheLineBits - 1));
        int step = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);
        int bit = (start + probeIndex * step) & (CacheLineBits - 1);
        laneIndex = bit >> 6;
        mask = 1UL << (bit & 63);
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private void WaitForBarrierToClear() { var sw = new SpinWait(); while (Volatile.Read(ref _stopWriters) != 0) sw.SpinOnce(); }
    private void WaitForWritersToDrain() { var sw = new SpinWait(); while (Volatile.Read(ref _writersInFlight) != 0) sw.SpinOnce(); }
    private void WaitForOpsToDrain() { var sw = new SpinWait(); while (Volatile.Read(ref _opsInFlight) != 0) sw.SpinOnce(); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposingOrDisposed, 1) != 0) return;
        Volatile.Write(ref _stopWriters, 1);

        WaitForWritersToDrain();
        WaitForOpsToDrain();

        using var _ = _maintenance.EnterScope();
        Flush();

        _accessor.Dispose();
        _mmf.Dispose();
        _fileHandle.Dispose();
        _disposed = true;
    }
}
