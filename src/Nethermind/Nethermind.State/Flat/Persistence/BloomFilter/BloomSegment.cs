// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotNext.IO.MemoryMappedFiles;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class BloomSegment : IDisposable
{
    // ---- layout / constants ----
    private const int CacheLineBytes = 64;      // 512 bits
    private const int CacheLineBits  = 512;
    private const int HeaderSize     = 128;

    // golden ratio for probe dispersion
    private const ulong ProbeDelta = 0x9E3779B97F4A7C15UL;

    // ---- WAL constants ----
    private const double DefaultWalThresholdPercent = 0.01; // 1% of capacity
    private const int WalEntrySize = 8; // ulong hash

    // ---- persisted metadata ----
    public long Capacity { get; set; }
    public int BitsPerKey { get; set; }
    public int K { get; set; }

    // Count/IsSealed must be thread-safe
    private long _count;
    public long Count => Volatile.Read(ref _count);

    private int _sealed; // 0/1
    public bool IsSealed => Volatile.Read(ref _sealed) != 0;

    // ---- layout ----
    private readonly long _dataOffset;
    private long _numBlocks;

    // ---- mmap ----
    private readonly MemoryMappedFile _mmf;
    private MemoryMappedDirectAccessor _directAccessor;

    // ---- concurrency barrier for flush/seal/dispose ----
    private int _writersInFlight;
    private int _stopWriters; // 0 = normal, 1 = block new writers
    private readonly Lock _maintenance = new();

    // ops barrier to prevent dispose use-after-free (covers reads too)
    private int _opsInFlight;

    // lifecycle
    private int _isDisposingOrDisposed;  // 0/1

    private readonly string _path;
    private readonly string _walPath;

    // ---- WAL (Write-Ahead Log) ----
    private readonly double _walThresholdPercent;
    private long _walThreshold; // Max entries before triggering MMAP flush
    private FileStream? _walStream;
    private readonly Lock _walLock = new();
    private long _walEntryCount;

    private Task? _backgroundFlushTask;
    private readonly CancellationTokenSource _backgroundFlushCts = new();
    private int _flushInProgress; // 0/1 gate

    public BloomSegment(
        string path,
        long capacity,
        int bitsPerKey,
        bool createNew,
        double walThresholdPercent = DefaultWalThresholdPercent)
    {
        _path = path;
        _walPath = path + ".wal";
        Capacity   = capacity;
        BitsPerKey = bitsPerKey;
        K = Math.Max(1, (int)Math.Round(bitsPerKey * Math.Log(2)));

        _walThresholdPercent = walThresholdPercent;
        _walThreshold = Math.Max(1, (long)(capacity * walThresholdPercent));

        long totalBits;
        long totalBytes;

        checked
        {
            totalBits  = capacity * bitsPerKey;
            totalBytes = AlignUp((totalBits + 7) / 8, CacheLineBytes);
        }

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

        // DotNext: direct pointer + Span-based view (unsafe, no bounds checks)
        _directAccessor = _mmf.CreateDirectAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);

        ApplyMadvise((IntPtr)_directAccessor.Pointer.Address, _directAccessor.Size);

        if (createNew)
        {
            // start unsealed with count 0
            Volatile.Write(ref _sealed, 0);
            Volatile.Write(ref _count, 0);
            WriteHeader();
            _directAccessor.Flush();
            InitializeWal(createNew: true);
        }
        else
        {
            ReadHeaderAndRebuildLayout();
            ReplayWalIfExists();          // replays + updates count + deletes WAL to avoid re-inflation
            InitializeWal(createNew: false);
        }
    }

    private void InitializeWal(bool createNew)
    {
        using var _ = _walLock.EnterScope();

        if (createNew && File.Exists(_walPath))
        {
            File.Delete(_walPath);
        }

        _walStream = new FileStream(
            _walPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        _walEntryCount = _walStream.Length / WalEntrySize;
    }

    private void ReplayWalIfExists()
    {
        if (!File.Exists(_walPath))
            return;

        Console.Error.WriteLine($"Replaying WAL: {_walPath}");

        long entriesReplayed = 0;

        using (var walReader = new FileStream(
                   _walPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Span<byte> buffer = stackalloc byte[WalEntrySize];

            while (true)
            {
                int read = walReader.Read(buffer);
                if (read == 0) break;
                if (read != WalEntrySize)
                    throw new InvalidDataException("Corrupted WAL (partial entry)");

                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
                AddToMmapDirectly(hash);
                entriesReplayed++;
            }
        }

        if (entriesReplayed > 0)
            Interlocked.Add(ref _count, entriesReplayed);

        FlushMMAPDurable();

        // prevent re-inflation on next restart
        File.Delete(_walPath);

        Console.Error.WriteLine($"WAL replay complete: {entriesReplayed} entries");
    }

    private void FlushMMAPDurable()
    {
        // Flush MMAP to disk
        _directAccessor.Flush();

        // Fsync the underlying file
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.Flush(flushToDisk: true);
    }

    private static void WarmupFileSequential(string path)
    {
        const int BufferSize = 4 * 1024 * 1024;      // 4 MB
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

        Console.Error.WriteLine($"Bloom warmup start: {fileSize / (1024 * 1024)} MB");

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
    private static extern int madvise(IntPtr addr, UIntPtr length, int advice);

    private static void ApplyMadvise(IntPtr address, long length)
    {
        if (!OperatingSystem.IsLinux())
            return;

        madvise(address, (UIntPtr)length, MADV_RANDOM);
        madvise(address, (UIntPtr)length, MADV_HUGEPAGE);
    }

    // ---------------------------------------------------------------------
    // public API
    // ---------------------------------------------------------------------

    public unsafe bool MightContain(ulong h1)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(BloomSegment));

        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);

        byte* blockPtr = (byte*)_directAccessor.Pointer.Address + baseOffset;

        // Load the whole 64-byte block once (8 lanes)
        ulong l0 = *(ulong*)(blockPtr + 0);
        ulong l1 = *(ulong*)(blockPtr + 8);
        ulong l2 = *(ulong*)(blockPtr + 16);
        ulong l3 = *(ulong*)(blockPtr + 24);
        ulong l4 = *(ulong*)(blockPtr + 32);
        ulong l5 = *(ulong*)(blockPtr + 40);
        ulong l6 = *(ulong*)(blockPtr + 48);
        ulong l7 = *(ulong*)(blockPtr + 56);

        for (int i = 0; i < K; i++)
        {
            GetLaneInBlock(h2, i, out int laneIndex, out ulong mask);

            ulong laneVal = laneIndex switch
            {
                0 => l0, 1 => l1, 2 => l2, 3 => l3,
                4 => l4, 5 => l5, 6 => l6, _ => l7
            };

            if ((laneVal & mask) == 0)
                return false;
        }

        return true;
    }

    // Concurrent-safe: atomic OR on 64-bit lanes + atomic count.
    public unsafe void Add(ulong h1)
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            throw new ObjectDisposedException(nameof(BloomSegment));

        // Fast fail
        if (Volatile.Read(ref _sealed) != 0)
            throw new InvalidOperationException("Segment is sealed");

        Interlocked.Increment(ref _opsInFlight);
        try
        {
            // Admit protocol: register writer first, then validate barrier.
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
                WaitForBarrierToClearOrSealedOrDisposed();
            }

            try
            {
                // WAL FIRST (so crash recovery can always reconstruct if MMAP wasn't flushed)
                WriteToWal(h1);

                // THEN update MMAP
                AddToMmapDirectly(h1);

                Interlocked.Increment(ref _count);

                if (Volatile.Read(ref _walEntryCount) >= _walThreshold)
                    TriggerBackgroundFlushIfNeeded();
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

    private unsafe void AddToMmapDirectly(ulong h1)
    {
        GetBlockAndHashes(h1, out ulong h2, out long baseOffset);

        byte* blockPtr = (byte*)_directAccessor.Pointer.Address + baseOffset;

        // compute once per key (fewer ops than calling GetLaneInBlock every time)
        int start = (int)(h2 & (CacheLineBits - 1));
        int step  = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);

        for (int i = 0; i < K; i++)
        {
            int bit = (start + i * step) & (CacheLineBits - 1);
            int laneIndex = bit >> 6;
            ulong mask = 1UL << (bit & 63);

            // baseOffset is 64-byte aligned; laneIndex*8 is 8-byte aligned
            ref long lane = ref *(long*)(blockPtr + (laneIndex * 8));

            // atomic set of bits (prevents lost updates)
            Interlocked.Or(ref lane, (long)mask);
        }
    }

    private void WriteToWal(ulong hash)
    {
        using var _ = _walLock.EnterScope();

        if (_walStream == null)
            return;

        Span<byte> buffer = stackalloc byte[WalEntrySize];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, hash);

        _walStream.Write(buffer);
        // still track with atomic because readers are outside wal lock (threshold check)
        Interlocked.Increment(ref _walEntryCount);
    }

    private void TriggerBackgroundFlushIfNeeded()
    {
        if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
            return;

        // Single-flight background flush
        if (Interlocked.CompareExchange(ref _flushInProgress, 1, 0) != 0)
            return;

        _backgroundFlushTask = Task.Run(() =>
        {
            try
            {
                FlushMmapAndRotateWal();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Background MMAP flush error: {ex}");
            }
            finally
            {
                Volatile.Write(ref _flushInProgress, 0);
            }
        }, _backgroundFlushCts.Token);
    }

    private void FlushMmapAndRotateWal()
    {
        // Rare path: serialize with maintenance lock
        using var _ = _maintenance.EnterScope();

        // Block new writers
        Volatile.Write(ref _stopWriters, 1);

        // Wait for existing writers (safe: Add registers before barrier check)
        WaitForWritersToDrain_NoLock();

        try
        {
            if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
                return;

            // Persist metadata BEFORE flushing pages
            WriteHeader();

            FlushMMAPDurable();

            // Rotate WAL
            using (var walLock = _walLock.EnterScope())
            {
                _walStream?.Flush(flushToDisk: true);
                _walStream?.Dispose();
                _walStream = null;

                // Delete old WAL
                if (File.Exists(_walPath))
                    File.Delete(_walPath);

                // Create new WAL (unless sealed/disposed)
                if (Volatile.Read(ref _sealed) == 0 &&
                    Volatile.Read(ref _isDisposingOrDisposed) == 0)
                {
                    _walStream = new FileStream(
                        _walPath,
                        FileMode.Create,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                }

                Volatile.Write(ref _walEntryCount, 0);
            }

            Console.Error.WriteLine($"MMAP flushed and WAL rotated for: {_path}");
        }
        finally
        {
            // Re-open for writers (if not sealed/disposed)
            if (Volatile.Read(ref _sealed) == 0 &&
                Volatile.Read(ref _isDisposingOrDisposed) == 0)
            {
                Volatile.Write(ref _stopWriters, 0);
            }
        }
    }

    public bool IsFull => Count >= Capacity;

    public void Seal()
    {
        // Serialize seal/flush/header with a short critical section.
        using var _ = _maintenance.EnterScope();

        // Block new writers, stop future Adds
        Volatile.Write(ref _stopWriters, 1);
        Volatile.Write(ref _sealed, 1);

        WaitForWritersToDrain_NoLock();

        // Persist metadata BEFORE flushing pages
        WriteHeader();
        _directAccessor.Flush();

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            fs.Flush(flushToDisk: true);

        // Sealed segment: delete WAL
        using (var walLock = _walLock.EnterScope())
        {
            _walStream?.Flush(flushToDisk: true);
            _walStream?.Dispose();
            _walStream = null;

            if (File.Exists(_walPath))
                File.Delete(_walPath);

            Volatile.Write(ref _walEntryCount, 0);
        }

        // Keep _stopWriters=1; sealed segment should not accept new writers anyway.
    }

    public void Flush()
    {
        // Only flush WAL, not MMAP (per WAL pattern)
        using var _ = _walLock.EnterScope();
        _walStream?.Flush(flushToDisk: true);
    }

    private void WaitForBarrierToClearOrSealedOrDisposed()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _stopWriters) != 0)
        {
            if (Volatile.Read(ref _sealed) != 0)
                throw new InvalidOperationException("Segment is sealed");

            if (Volatile.Read(ref _isDisposingOrDisposed) != 0)
                throw new ObjectDisposedException(nameof(BloomSegment));

            sw.SpinOnce();
        }
    }

    private void WaitForWritersToDrain_NoLock()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _writersInFlight) != 0)
            sw.SpinOnce();
    }

    private void WaitForOpsToDrain_NoLock()
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _opsInFlight) != 0)
            sw.SpinOnce();
    }

    // ---------------------------------------------------------------------
    // header persistence (unaligned reads/writes via DotNext pointer)
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadUInt64(long offset)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        return Unsafe.ReadUnaligned<ulong>(ref b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadInt64(long offset)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        return Unsafe.ReadUnaligned<long>(ref b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadInt32(long offset)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        return Unsafe.ReadUnaligned<int>(ref b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt64(long offset, ulong value)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        Unsafe.WriteUnaligned(ref b, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt64(long offset, long value)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        Unsafe.WriteUnaligned(ref b, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt32(long offset, int value)
    {
        var p = _directAccessor.Pointer;
        ref byte b = ref p[(nuint)offset];
        Unsafe.WriteUnaligned(ref b, value);
    }

    private void WriteHeader()
    {
        WriteUInt64(0,  0x424C4F4F4DUL); // "BLOOM"
        WriteInt64 (8,  Capacity);
        WriteInt32 (16, BitsPerKey);
        WriteInt32 (20, K);
        WriteInt64 (24, Count);
        WriteInt32 (32, IsSealed ? 1 : 0);
    }

    private void ReadHeaderAndRebuildLayout()
    {
        ulong magic = ReadUInt64(0);
        if (magic != 0x424C4F4F4DUL) // "BLOOM"
            throw new InvalidDataException("Invalid bloom segment header");

        Capacity   = ReadInt64(8);
        BitsPerKey = ReadInt32(16);
        K          = ReadInt32(20);

        long count = ReadInt64(24);
        int sealedFlag = ReadInt32(32);

        Volatile.Write(ref _count, count);
        Volatile.Write(ref _sealed, sealedFlag != 0 ? 1 : 0);

        // If sealed, keep writers blocked; otherwise allow.
        Volatile.Write(ref _stopWriters, Volatile.Read(ref _sealed) != 0 ? 1 : 0);

        if (Capacity <= 0 || BitsPerKey <= 0 || K <= 0)
            throw new InvalidDataException("Corrupted bloom header");

        checked
        {
            long totalBits  = Capacity * BitsPerKey;
            long totalBytes = AlignUp((totalBits + 7) / 8, CacheLineBytes);
            _numBlocks = totalBytes / CacheLineBytes;
        }

        _walThreshold = Math.Max(1, (long)(Capacity * _walThresholdPercent));
    }

    // ---------------------------------------------------------------------
    // helpers (tight, local, inlinable)
    // ---------------------------------------------------------------------

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

    // 64-bit lane addressing within a 64-byte block (8 lanes)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetLaneInBlock(ulong h2, int probeIndex, out int laneIndex, out ulong mask)
    {
        // 9-bit start (0..511)
        int start = (int)(h2 & (CacheLineBits - 1));

        // per-key odd step in 1..511 (odd => cycles through all 512 positions)
        int step = (int)(((h2 >> 9) & (CacheLineBits - 1)) | 1);

        int bit = (start + probeIndex * step) & (CacheLineBits - 1);
        laneIndex = bit >> 6;
        mask = 1UL << (bit & 63);
    }

    private static long AlignUp(long value, int alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    public void Dispose()
    {
        // One-way transition into disposing state
        if (Interlocked.Exchange(ref _isDisposingOrDisposed, 1) != 0)
            return;

        // Make any would-be waiters fail quickly
        Volatile.Write(ref _stopWriters, 1);

        // Wait flush
        _backgroundFlushTask?.Wait();

        // Drain active writers & readers
        WaitForWritersToDrain_NoLock();
        WaitForOpsToDrain_NoLock();

        // Finalize under maintenance lock (rare path)
        using var _ = _maintenance.EnterScope();

        // Persist header then flush mmap
        WriteHeader();

        FlushMMAPDurable();

        Volatile.Write(ref _sealed, 1);

        // Flush & close WAL
        using (var walLock = _walLock.EnterScope())
        {
            _walStream?.Flush(flushToDisk: true);
            _walStream?.Dispose();
            _walStream = null;

            if (File.Exists(_walPath))
                File.Delete(_walPath);
        }

        _directAccessor.Dispose();
        _mmf.Dispose();

        _backgroundFlushCts.Dispose();
    }

    /// <summary>
    /// Used by unit test to start for
    /// </summary>
    public void CloseWal()
    {
        _walStream?.Flush(flushToDisk: true);
        _walStream?.Dispose();
        _walStream = null;
    }
}
