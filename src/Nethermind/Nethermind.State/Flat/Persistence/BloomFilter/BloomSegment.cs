// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class BloomSegment : IDisposable
{
    // ---- layout / constants ----
    private const int CacheLineBytes = 64;      // 512 bits
    private const int CacheLineBits  = 512;
    private const int HeaderSize     = 128;

    // golden ratio for probe dispersion
    private const ulong ProbeDelta = 0x9E3779B97F4A7C15UL;

    // ---- persisted metadata ----
    public long Capacity { get; set; }
    public int BitsPerKey { get; set; }
    public int K { get; set; }
    public long Count { get; private set; }
    public bool IsSealed { get; private set; }

    // ---- layout ----
    private readonly long _dataOffset;
    private long _numBlocks;

    // ---- mmap ----
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;

    // ---------------------------------------------------------------------

    public BloomSegment(
        string path,
        long capacity,
        int bitsPerKey,
        bool createNew)
    {
        Capacity   = capacity;
        BitsPerKey = bitsPerKey;
        K = Math.Max(1, (int)Math.Round(bitsPerKey * Math.Log(2)));

        long totalBits  = capacity * bitsPerKey;
        long totalBytes = AlignUp((totalBits + 7) / 8, CacheLineBytes);

        _numBlocks  = totalBytes / CacheLineBytes;
        _dataOffset = AlignUp(HeaderSize, CacheLineBytes);
        long fileSize = _dataOffset + totalBytes;

        if (File.Exists(path))
        {
            fileSize = Math.Max(new FileInfo(path).Length, fileSize);
            WarmupFileSequential(path);
        }

        _mmf = MemoryMappedFile.CreateFromFile(
            path,
            createNew ? FileMode.Create : FileMode.Open,
            null,
            fileSize,
            MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, fileSize);

        unsafe
        {
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            try
            {
                ApplyMadvise((IntPtr)ptr, _view.Capacity);
            }
            finally
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        if (createNew)
            WriteHeader();
        else
            ReadHeaderAndRebuildLayout();

        Console.Error.WriteLine($"The pram {BitsPerKey}, {Count}, {K}");
    }


    private static void WarmupFileSequential(string path)
    {
        const int BufferSize = 4 * 1024 * 1024; // 4 MB
        const long LogEveryBytes = 10 * 1024 * 1024; // 10 MB

        using FileStream fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan);

        byte[] buffer = new byte[BufferSize];

        long totalRead = 0;
        long fileSize = fs.Length;
        long nextLog = LogEveryBytes;

        Console.Error.WriteLine(
            $"Bloom warmup start: {fileSize / (1024 * 1024)} MB");

        while (true)
        {
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            totalRead += read;

            if (totalRead >= nextLog)
            {
                Console.Error.WriteLine(
                    $"Bloom warmup {totalRead / (1024 * 1024)} MB / {fileSize / (1024 * 1024)} MB");
                nextLog += LogEveryBytes;
            }
        }

        Console.Error.WriteLine("Bloom warmup complete");
    }

    private const int MADV_RANDOM   = 1;
    private const int MADV_HUGEPAGE = 14;

    [DllImport("libc", SetLastError = true)]
    private static extern int madvise(
        IntPtr addr,
        UIntPtr length,
        int advice);
    private void ApplyMadvise(IntPtr address, long length)
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Dont try readahead
        madvise(address, (UIntPtr)length, MADV_RANDOM);

        // Use THP if possible
        madvise(address, (UIntPtr)length, MADV_HUGEPAGE);
    }

    // ---------------------------------------------------------------------
    // public API
    // ---------------------------------------------------------------------

    public bool MightContain(ulong h1)
    {
        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);

        for (int i = 0; i < K; i++)
        {
            GetBitInBlock(h2, i, out int byteIndex, out byte mask);

            if ((_view.ReadByte(baseOffset + byteIndex) & mask) == 0)
                return false;
        }

        return true;
    }

    public void Add(ulong h1)
    {
        if (IsSealed)
            throw new InvalidOperationException("Segment is sealed");

        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);

        for (int i = 0; i < K; i++)
        {
            GetBitInBlock(h2, i, out int byteIndex, out byte mask);

            byte b = _view.ReadByte(baseOffset + byteIndex);
            _view.Write(baseOffset + byteIndex, (byte)(b | mask));
        }

        Count++;
    }

    public bool IsFull => Count >= Capacity;

    public void Seal()
    {
        IsSealed = true;
        WriteHeader();
        _view.Flush();
    }

    public void Flush()
    {
        _view.Flush();
    }

    // ---------------------------------------------------------------------
    // header persistence
    // ---------------------------------------------------------------------

    private void WriteHeader()
    {
        _view.Write(0,  0x424C4F4F4DUL); // "BLOOM"
        _view.Write(8,  Capacity);
        _view.Write(16, BitsPerKey);
        _view.Write(20, K);
        _view.Write(24, Count);
        _view.Write(32, IsSealed ? 1 : 0);
    }

    private void ReadHeaderAndRebuildLayout()
    {
        ulong magic = _view.ReadUInt64(0);
        if (magic != 0x424C4F4F4DUL) // "BLOOM"
            throw new InvalidDataException("Invalid bloom segment header");

        Capacity   = _view.ReadInt64(8);
        BitsPerKey = _view.ReadInt32(16);
        K          = _view.ReadInt32(20);
        Count      = _view.ReadInt64(24);
        IsSealed   = _view.ReadInt32(32) != 0;

        if (Capacity <= 0 || BitsPerKey <= 0 || K <= 0)
            throw new InvalidDataException("Corrupted bloom header");

        // Recompute layout from persisted values
        long totalBits  = Capacity * BitsPerKey;
        long totalBytes = AlignUp((totalBits + 7) / 8, CacheLineBytes);

        _numBlocks = totalBytes / CacheLineBytes;
    }

    // ---------------------------------------------------------------------
    // helpers (tight, local, inlinable)
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetBlockAndHashes(
        ulong h1,
        out ulong h2,
        out long baseOffset)
    {
        h2 = Mix(h1);

        long block = (long)(h1 % (ulong)_numBlocks);
        baseOffset = _dataOffset + block * CacheLineBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h)
        => (h >> 17) | (h << 47);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetBitInBlock(
        ulong h2,
        int probeIndex,
        out int byteIndex,
        out byte mask)
    {
        int bit = (int)((h2 + (ulong)probeIndex * ProbeDelta) & (CacheLineBits - 1));
        byteIndex = bit >> 3;
        mask = (byte)(1 << (bit & 7));
    }

    private static long AlignUp(long value, int alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    // ---------------------------------------------------------------------

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
