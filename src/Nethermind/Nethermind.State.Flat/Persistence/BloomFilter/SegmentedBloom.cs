// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Prometheus;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class SegmentedBloom : IDisposable
{
    // Serializes rotation + segment list mutation + snapshot publishing
    private readonly Lock _segmentsLock = new();

    // Serializes WAL appends + coordinates checkpoints with concurrent Add()
    private readonly Lock _walLock = new();

    private readonly string _directory;
    private readonly long _segmentCapacity;
    private readonly int _bitsPerKey;

    private readonly List<PersistedBloomFilter> _segments = new();
    private volatile PersistedBloomFilter[] _snapshot = Array.Empty<PersistedBloomFilter>();
    private volatile PersistedBloomFilter _current = null!;

    private static Gauge _bloomKeySize = DevMetric.Factory.CreateGauge("segmented_bloom_counts", "", "type");
    private readonly Gauge.Child _total = _bloomKeySize.WithLabels("total");
    private readonly Gauge.Child _skipped = _bloomKeySize.WithLabels("skipped");

    private static Histogram _bloomTime = DevMetric.Factory.CreateHistogram("segmented_bloom_read_time", "", new HistogramConfiguration()
    {
        LabelNames = ["hitmiss"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 9, 3),
    });

    private readonly Gauge _unwrittenEntries = DevMetric.Factory.CreateGauge("segmented_bloom_unwritten_entries", "");

    private readonly Histogram.Child _bloomHitTime = _bloomTime.WithLabels("hit");
    private readonly Histogram.Child _bloomMissTime = _bloomTime.WithLabels("miss");

    private readonly bool _enabled;

    // --- WAL state ---
    private readonly bool _walEnabled;
    private readonly string _walPath;
    private FileStream? _walStream;

    // Flush WAL occasionally to reduce data loss window without fsync-ing every Add().
    // Tune as needed.
    private readonly int _walFlushThresholdBytes;
    private int _walBytesSinceTruncate;

    // If you need stronger durability guarantees (at a performance cost), flip this to true.
    // true => FileStream.Flush(flushToDisk: true) at threshold/checkpoint.
    private const bool WalFlushToDisk = false;

    public SegmentedBloom(
        string directory,
        long segmentCapacity,
        int bitsPerKey,
        bool enabled = true,
        int walFlushThresholdBytes = -1)
    {
        _enabled = enabled;

        if (walFlushThresholdBytes == -1)
        {
            walFlushThresholdBytes = (int)((segmentCapacity * 0.01) * sizeof(ulong));
            Console.Error.WriteLine($"Wal flush is set to {walFlushThresholdBytes:N0}");
        }

        _walEnabled = walFlushThresholdBytes > 0;
        _walFlushThresholdBytes = walFlushThresholdBytes;
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        _bitsPerKey = bitsPerKey;

        _walPath = Path.Combine(directory, "segmented_bloom.wal");

        if (!_enabled) return;

        Directory.CreateDirectory(directory);

        // Load segments
        using (var _ = _segmentsLock.EnterScope())
        {
            foreach (var file in Directory.GetFiles(_directory, "*.bloom"))
            {
                var seg = PersistedBloomFilter.OpenExisting(file);
                _total.Inc(seg.Count);

                _segments.Add(seg);
                if (!seg.IsSealed)
                    _current = seg;
            }

            _segments.Sort((a, b) => b.Count.CompareTo(a.Count));

            if (_current is null)
                _current = CreateNewSegment_NoLock();

            _snapshot = _segments.ToArray();
        }

        // WAL (optional): open, replay pending adds, then checkpoint (flush segments + truncate WAL)
        if (_walEnabled)
        {
            OpenWal();
            ReplayWal();
            Checkpoint(); // makes replay idempotent and keeps WAL bounded
        }
    }

    public bool IsEnabled => _enabled;

    public bool MightContain(ulong h1)
    {
        if (!_enabled) return true;

        long sw = Stopwatch.GetTimestamp();

        var segs = _snapshot; // lock-free snapshot
        foreach (var seg in segs)
        {
            if (seg.MightContain(h1))
            {
                _bloomHitTime.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }
        }

        _bloomMissTime.Observe(Stopwatch.GetTimestamp() - sw);
        return false;
    }

    /// <summary>
    /// Adds with WAL enabled (if globally enabled).
    /// </summary>
    public void Add(ulong h1) => Add(h1, writeWal: true);

    /// <summary>
    /// Adds with per-call control of WAL. If WAL is globally disabled, writeWal is ignored.
    /// </summary>
    public void Add(ulong h1, bool writeWal)
    {
        AddCore(h1, writeWal: writeWal && _walEnabled, recordMetrics: true);
    }

    private void AddCore(ulong h1, bool writeWal, bool recordMetrics)
    {
        if (!_enabled) return;

        // Keep existing behavior: if it already "might" exist, skip the write.
        if (MightContain(h1))
        {
            if (recordMetrics) _skipped.Inc();
            return;
        }

        // WAL first (after deciding we intend to add), then apply to bloom.
        if (writeWal)
            AppendWal(h1);

        if (recordMetrics) _total.Inc();

        const int maxAttemptCount = 3;
        for (int attempt = 0; attempt < maxAttemptCount; attempt++)
        {
            PersistedBloomFilter seg;
            if (attempt == maxAttemptCount - 1)
            {
                using var _r = _segmentsLock.EnterScope();
                seg = _current;
            }
            else
            {
                seg = _current;
            }

            if (!seg.TryAdd(h1))
            {
                // Rotation raced us: old current is sealed.
                if (seg.IsSealed)
                {
                    Thread.Sleep(1);
                    continue;
                }

                // Anything else is unexpected in normal operation (disposed/barrier, etc.)
                throw new InvalidOperationException("Failed to add to bloom segment.");
            }

            if (seg.IsFull) RotateIfNeeded(seg);

            return;
        }

        // If we keep losing the race, surface it.
        throw new InvalidOperationException("Failed to add after repeated concurrent rotations.");
    }

    private void RotateIfNeeded(PersistedBloomFilter observedCurrent)
    {
        using var _r = _segmentsLock.EnterScope();

        // Another thread may already have rotated.
        if (!ReferenceEquals(_current, observedCurrent))
        {
            return;
        }

        // Re-check under the rotation lock.
        if (observedCurrent.IsSealed)
        {
            return;
        }

        observedCurrent.Seal();
        _current = CreateNewSegment_NoLock();
        _snapshot = _segments.ToArray();
    }

    private PersistedBloomFilter CreateNewSegment_NoLock()
    {
        string path = Path.Combine(_directory, $"segment_{DateTime.UtcNow.Ticks}.bloom");
        var seg = PersistedBloomFilter.CreateNew(path, _segmentCapacity, _bitsPerKey);
        _segments.Insert(0, seg);
        return seg;
    }

    /// <summary>
    /// If WAL is enabled: flush WAL only (keep segments in memory; no checkpoint).
    /// If WAL is disabled: flush segments (best-effort durability).
    /// </summary>
    public void Flush()
    {
        if (!_enabled) return;

        if (_walEnabled)
        {
            if (_walBytesSinceTruncate > _walFlushThresholdBytes)
            {
                Console.Error.WriteLine($"Checkpoint");
                long sw = Stopwatch.GetTimestamp();
                Checkpoint();
                Console.Error.WriteLine($"Checkpoint took {Stopwatch.GetElapsedTime(sw)}");
            }
            else
            {
                FlushWalOnly();
            }
            return;
        }

        using var _s = _segmentsLock.EnterScope();
        foreach (var seg in _snapshot)
            seg.Flush();
    }

    private void FlushWalOnly()
    {
        using var _w = _walLock.EnterScope();
        if (_walStream is null) return;

        _walStream.Flush(flushToDisk: WalFlushToDisk);
    }

    /// <summary>
    /// Durable flush:
    /// - WAL enabled: flush segments and checkpoint WAL (truncate).
    /// - WAL disabled: flush segments.
    /// </summary>
    public void FlushDurable()
    {
        if (!_enabled) return;

        if (_walEnabled)
        {
            Checkpoint();
            return;
        }

        using var _s = _segmentsLock.EnterScope();
        foreach (var seg in _snapshot)
            seg.Flush();
    }

    public void Dispose()
    {
        if (!_enabled) return;

        // Best-effort final durability.
        try
        {
            if (_walEnabled) Checkpoint();
            else
            {
                using var _s = _segmentsLock.EnterScope();
                foreach (var seg in _snapshot)
                    seg.Flush();
            }
        }
        catch { /* swallow on dispose */ }

        using (var _s = _segmentsLock.EnterScope())
        {
            foreach (var seg in _snapshot)
                seg.Dispose();
        }

        if (_walEnabled)
        {
            using var _w = _walLock.EnterScope();
            _walStream?.Dispose();
            _walStream = null;
        }
    }

    // ---------------- WAL helpers ----------------

    private void OpenWal()
    {
        _walStream = new FileStream(
            _walPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
    }

    private void ReplayWal()
    {
        if (!_walEnabled || _walStream is null) return;

        // No concurrency during ctor, but keep it disciplined.
        using var _w = _walLock.EnterScope();

        long len = _walStream.Length;
        if (len <= 0)
        {
            _walStream.Position = len;
            return;
        }

        // Records are fixed 8-byte ulongs. Ignore any trailing partial record.
        long full = len - (len % sizeof(ulong));
        if (full <= 0)
        {
            _walStream.Position = len;
            return;
        }

        _walStream.Position = 0;

        byte[] buffer = new byte[64 * 1024];
        long remaining = full;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = _walStream.Read(buffer, 0, toRead);
            if (read <= 0) break;

            int usable = read - (read % sizeof(ulong));
            for (int i = 0; i < usable; i += sizeof(ulong))
            {
                ulong h1 = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, sizeof(ulong)));
                // Reapply without writing back to the WAL; don't distort metrics on recovery.
                AddCore(h1, writeWal: false, recordMetrics: false);
            }

            remaining -= usable;

            // If we read an odd chunk (shouldn't happen with FileStream), reposition to keep alignment.
            if (usable != read)
                _walStream.Position -= (read - usable);
        }

        // Set up for appends after replay.
        _walStream.Position = len;
        _walBytesSinceTruncate = 0;
    }

    private void AppendWal(ulong h1)
    {
        if (!_walEnabled || _walStream is null) return;

        using var _w = _walLock.EnterScope();

        Span<byte> tmp = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, h1);

        _walStream.Write(tmp);
        _walBytesSinceTruncate += sizeof(ulong);
        _unwrittenEntries.Inc(sizeof(ulong));
    }

    private void Checkpoint()
    {
        if (!_walEnabled) return;

        // Lock ordering rule: WAL lock first, then segments lock.
        using var _w = _walLock.EnterScope();
        using var _s = _segmentsLock.EnterScope();

        // Flush segments first so WAL truncation doesn't lose data.
        foreach (var seg in _snapshot)
            seg.Flush();

        if (_walStream is null) return;

        _walStream.Flush(flushToDisk: WalFlushToDisk);
        _walStream.SetLength(0);
        _walStream.Position = 0;
        _walBytesSinceTruncate = 0;
        _unwrittenEntries.Set(0);
    }
}
