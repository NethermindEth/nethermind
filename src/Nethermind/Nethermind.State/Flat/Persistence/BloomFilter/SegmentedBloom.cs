// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Prometheus;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

public sealed class SegmentedBloom : IDisposable
{
    // Serializes rotation + segment list mutation + snapshot publishing
    private readonly Lock _segmentsLock = new();

    readonly string _directory;
    readonly long _segmentCapacity;
    readonly int _bitsPerKey;

    readonly List<BloomSegment> _segments = new();
    private volatile BloomSegment[] _snapshot = Array.Empty<BloomSegment>();
    private volatile BloomSegment _current;

    private static Gauge _bloomKeySize = Metrics.CreateGauge("segmented_bloom_counts", "", "type");
    private readonly Gauge.Child _total = _bloomKeySize.WithLabels("total");
    private readonly Gauge.Child _skipped = _bloomKeySize.WithLabels("skipped");

    private static Histogram _bloomTime = Metrics.CreateHistogram("segmented_bloom_read_time", "", new HistogramConfiguration()
    {
        LabelNames = ["hitmiss"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 9, 3),
    });

    private readonly Histogram.Child _bloomHitTime = _bloomTime.WithLabels("hit");
    private readonly Histogram.Child _bloomMissTime = _bloomTime.WithLabels("miss");
    private readonly bool _enabled;

    public SegmentedBloom(string directory, long segmentCapacity, int bitsPerKey, bool enabled = true)
    {
        _enabled = enabled;
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        _bitsPerKey = bitsPerKey;

        if (!_enabled) return;

        Directory.CreateDirectory(directory);

        using var _ = _segmentsLock.EnterScope();
        foreach (var file in Directory.GetFiles(_directory, "*.bloom"))
        {
            var seg = new BloomSegment(file, capacity: 0, bitsPerKey: 0, createNew: false);
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

    public void Add(ulong h1)
    {
        if (!_enabled) return;

        if (MightContain(h1))
        {
            _skipped.Inc();
            return;
        }

        _total.Inc();

        const int maxAttemptCount = 3;
        for (int attempt = 0; attempt < maxAttemptCount; attempt++)
        {
            BloomSegment seg;
            if (attempt == maxAttemptCount - 1)
            {
                using var _r = _segmentsLock.EnterScope();
                seg = _current;
            }
            else
            {
                seg = _current;
            }

            try
            {
                seg.Add(h1);
            }
            catch (InvalidOperationException) when (seg.IsSealed)
            {
                // Rotation raced us. Try again with the new current.
                Thread.Sleep(1);
                continue;
            }

            if (seg.IsFull)
                RotateIfNeeded(seg);

            return;
        }

        // If we keep losing the race, surface it.
        throw new InvalidOperationException("Failed to add after repeated concurrent rotations.");
    }

    private void RotateIfNeeded(BloomSegment observedCurrent)
    {
        using var _r = _segmentsLock.EnterScope();

        // Another thread may already have rotated.
        if (!ReferenceEquals(_current, observedCurrent))
            return;

        // Re-check under the rotation lock.
        if (observedCurrent.IsSealed)
            return;

        observedCurrent.Seal();
        _current = CreateNewSegment_NoLock();
        _snapshot = _segments.ToArray();
    }

    private BloomSegment CreateNewSegment_NoLock()
    {
        string path = Path.Combine(_directory, $"segment_{DateTime.UtcNow.Ticks}.bloom");
        var seg = new BloomSegment(path, _segmentCapacity, _bitsPerKey, createNew: true);
        _segments.Insert(0, seg);
        return seg;
    }

    public void Flush()
    {
        if (!_enabled) return;
        using var _s = _segmentsLock.EnterScope();

        foreach (var seg in _snapshot)
            seg.Flush();
    }

    public void Dispose()
    {
        if (!_enabled) return;
        using var _s = _segmentsLock.EnterScope();

        foreach (var seg in _snapshot)
            seg.Dispose();
    }
}
