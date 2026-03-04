// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

/// <summary>
/// Persisted wrapper around SegmentBloom (file-backed durability).
/// Owns a SegmentBloom and forwards MightContain/TryAdd; maintains dirty pages + header + flush/seal barriers.
/// </summary>
public sealed unsafe class PersistedBloomFilter : IDisposable
{
    // ---- constants ----
    private const int CacheLineBytes = 64;
    private const int HeaderSize     = 128;
    private const int PageSize       = 4096;
    private const int ShiftPerPage   = 12;
    private const ulong Magic        = 0x424C4F4F4DUL; // "BLOOM"

    private readonly string _path;
    private readonly SafeFileHandle _fileHandle;

    private readonly long _dataOffset;
    private readonly long _totalFileSize;
    private readonly ulong[] _dirtyPages;

    private readonly BloomFilter _bloomFilter;

    private int _sealed; // 0/1
    public bool IsSealed => Volatile.Read(ref _sealed) != 0;

    private int _stopWriters;
    private int _writersInFlight;
    private int _opsInFlight;
    private int _isDisposingOrDisposed;

    private readonly Lock _maintenance = new();

    public long Capacity => _bloomFilter.Capacity;
    public double BitsPerKey => _bloomFilter.BitsPerKey;
    public int K => _bloomFilter.K;
    public long Count => _bloomFilter.Count;
    public string Path => _path;
    public bool IsFull => Count >= Capacity;

    private PersistedBloomFilter(string path, SafeFileHandle fileHandle, BloomFilter bloomFilter, bool isSealed)
    {
        _path = path;
        _fileHandle = fileHandle;
        _bloomFilter = bloomFilter;

        _sealed = isSealed ? 1 : 0;
        _stopWriters = isSealed ? 1 : 0;

        _dataOffset = AlignUp(HeaderSize, CacheLineBytes);
        _totalFileSize = _dataOffset + _bloomFilter.DataBytes;

        long pageCount = (_totalFileSize + PageSize - 1) / PageSize;
        _dirtyPages = new ulong[(pageCount + 63) / 64];
    }

    public static PersistedBloomFilter CreateNew(string path, long capacity, double bitsPerKey)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);

        var bloom = new BloomFilter(capacity, bitsPerKey, initialCount: 0);
        var seg = new PersistedBloomFilter(path, handle, bloom, isSealed: false);

        // Pre-size file so later random writes are valid
        RandomAccess.SetLength(handle, seg._totalFileSize);

        // Persist initial header; we don't need to write the whole zeroed data.
        seg.WriteHeaderToDisk();
        return seg;
    }

    public static PersistedBloomFilter OpenExisting(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Span<byte> hdr = stackalloc byte[HeaderSize];
        RandomAccess.Read(handle, hdr, 0);

        if (BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(0, 8)) != Magic)
            throw new InvalidDataException("Invalid bloom segment header");

        long capacity = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(8, 8));

        // bits-per-key is stored as float32 at [16], with fallback to legacy int32
        float bpk32 = BinaryPrimitives.ReadSingleLittleEndian(hdr.Slice(16, 4));
        double bitsPerKey;
        if (bpk32 > 0.0f && !float.IsNaN(bpk32) && !float.IsInfinity(bpk32))
        {
            bitsPerKey = bpk32;
        }
        else
        {
            int oldBpk = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(16, 4));
            if (oldBpk <= 0) throw new InvalidDataException("Invalid BitsPerKey in header");
            bitsPerKey = oldBpk;
        }

        int k = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(20, 4));
        long count = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(24, 8));
        bool isSealed = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(32, 4)) != 0;

        // Construct bloom and force persisted K
        var bloom = new BloomFilter(capacity, bitsPerKey, initialCount: count)
        {
            // we want exact behavior on existing files
            // (K is get-only; so instead, we validate and rely on persisted data by warning)
        };

        // If you require exact K across restarts, store it and use it. SegmentBloom currently chooses K in ctor.
        // We'll enforce by validating and throwing if mismatch (safer than silently changing behavior).
        if (bloom.K != k)
            throw new InvalidDataException($"Persisted K={k} does not match computed K={bloom.K} for bitsPerKey={bitsPerKey}.");

        var seg = new PersistedBloomFilter(path, handle, bloom, isSealed);

        // Load data region from file into bloom memory
        seg.LoadDataFromFile();
        return seg;
    }

    public bool MightContain(ulong key)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(PersistedBloomFilter));

        Interlocked.Increment(ref _opsInFlight);
        try
        {
            return _bloomFilter.MightContain(key);
        }
        finally
        {
            Interlocked.Decrement(ref _opsInFlight);
        }
    }

    public bool TryAdd(ulong key)
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

                if (Volatile.Read(ref _sealed) != 0 || Volatile.Read(ref _isDisposingOrDisposed) != 0)
                    return false;

                return false;
            }

            try
            {
                long dataLineOffset = _bloomFilter.Add(key); // forwards to in-memory bloom
                MarkPageDirty(_dataOffset + dataLineOffset);
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
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(PersistedBloomFilter));

        using var _ = _maintenance.EnterScope();
        DoFlush_NoLock();
    }

    public void Seal()
    {
        using var _ = _maintenance.EnterScope();
        Volatile.Write(ref _stopWriters, 1);
        Volatile.Write(ref _sealed, 1);

        WaitForWritersToDrain();
        DoFlush_NoLock();
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
            DoFlush_NoLock();
        }
        catch
        {
            // best-effort
        }

        _bloomFilter.Dispose();
        _fileHandle.Dispose();
    }

    // ---------------- persistence internals ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkPageDirty(long fileOffset)
    {
        long pageIdx = fileOffset >> ShiftPerPage;
        int wordIdx = (int)(pageIdx >> 6);
        ulong mask = 1UL << (int)(pageIdx & 63);

        ref long word = ref Unsafe.As<ulong, long>(ref _dirtyPages[wordIdx]);
        Interlocked.Or(ref word, (long)mask);
    }

    private void DoFlush_NoLock()
    {
        // Always make sure header is durable
        MarkPageDirty(0);
        Span<byte> page = stackalloc byte[PageSize];

        // Write dirty pages
        for (int i = 0; i < _dirtyPages.Length; i++)
        {
            ulong word = (ulong)Interlocked.Exchange(ref Unsafe.As<ulong, long>(ref _dirtyPages[i]), 0);
            if (word == 0) continue;

            while (word != 0)
            {
                int b = BitOperations.TrailingZeroCount(word);
                long pageIdx = (long)i * 64 + b;
                long offset = pageIdx * PageSize;

                long remaining = _totalFileSize - offset;
                if (remaining <= 0)
                {
                    word &= (word - 1);
                    continue;
                }

                int sizeToWrite = (int)Math.Min(PageSize, remaining);

                BuildFileView(offset, page.Slice(0, sizeToWrite));

                RandomAccess.Write(_fileHandle, page.Slice(0, sizeToWrite), offset);

                word &= (word - 1);
            }
        }

        RandomAccess.FlushToDisk(_fileHandle);
    }

    private void WriteHeaderToDisk()
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        hdr.Clear();

        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(0, 8), Magic);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(8, 8), _bloomFilter.Capacity);

        // store bits-per-key as float32 for compactness + stable layout
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(16, 4), (float)_bloomFilter.BitsPerKey);

        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(20, 4), _bloomFilter.K);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(24, 8), _bloomFilter.Count);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(32, 4), IsSealed ? 1 : 0);

        RandomAccess.Write(_fileHandle, hdr, 0);
        RandomAccess.FlushToDisk(_fileHandle);
    }

    private void LoadDataFromFile()
    {
        byte* dst = _bloomFilter.DangerousGetDataPointer();
        long total = _bloomFilter.DataBytes;

        const int Chunk = 4 * 1024 * 1024;
        long fileOffset = _dataOffset;
        long done = 0;

        while (done < total)
        {
            int len = (int)Math.Min(Chunk, total - done);
            Span<byte> buf = new(dst + done, len);
            int read = RandomAccess.Read(_fileHandle, buf, fileOffset + done);
            if (read <= 0) break;
            done += read;
        }
    }

    /// <summary>
    /// Build a contiguous view of the "virtual file" bytes at [fileOffset..fileOffset+dest.Length),
    /// combining header (generated from current state) and bloom data bytes.
    /// </summary>
    private void BuildFileView(long fileOffset, Span<byte> dest)
    {
        // Build current header bytes on demand
        Span<byte> hdr = stackalloc byte[HeaderSize];
        hdr.Clear();

        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(0, 8), Magic);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(8, 8), _bloomFilter.Capacity);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(16, 4), (float)_bloomFilter.BitsPerKey);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(20, 4), _bloomFilter.K);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(24, 8), _bloomFilter.Count);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(32, 4), IsSealed ? 1 : 0);

        long destLen = dest.Length;
        long off = fileOffset;

        // Copy header portion if any
        if (off < _dataOffset)
        {
            int headerAvail = (int)Math.Min(destLen, _dataOffset - off);
            hdr.Slice((int)off, headerAvail).CopyTo(dest.Slice(0, headerAvail));
            off += headerAvail;
            dest = dest.Slice(headerAvail);
            if (dest.Length == 0) return;
        }

        // Copy bloom data portion
        long dataOff = off - _dataOffset;
        if (dataOff < 0) dataOff = 0;

        byte* src = _bloomFilter.DangerousGetDataPointer() + dataOff;
        new ReadOnlySpan<byte>(src, dest.Length).CopyTo(dest);
    }

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

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}

