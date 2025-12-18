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
    // Serializes actual writes into BloomSegment (required if BloomSegment is unchanged)
    private readonly Lock _writeLock = new();

    // Serializes segment list mutation + snapshot publishing + lifecycle ops
    private readonly Lock _segmentsLock = new();

    readonly string _directory;
    readonly long _segmentCapacity;
    readonly int _bitsPerKey;

    readonly List<BloomSegment> _segments = new();
    private volatile BloomSegment[] _snapshot = Array.Empty<BloomSegment>();
    private BloomSegment _current;

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

    public SegmentedBloom(string directory, long segmentCapacity, int bitsPerKey)
    {
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        _bitsPerKey = bitsPerKey;

        Directory.CreateDirectory(directory);

        using var _ = _segmentsLock.EnterScope();
        LoadExistingSegments_NoLock();

        if (_current == null)
            _current = CreateNewSegment_NoLock();

        PublishSnapshot_NoLock();
    }

    public bool MightContain(ulong h1)
    {
        long sw = Stopwatch.GetTimestamp();

        // lock-free, safe: snapshot never changes during enumeration
        var segs = _snapshot;
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
        // Keep your pre-check. (Note: two threads can still both miss and then both add.)
        if (MightContain(h1))
        {
            _skipped.Inc();
            return;
        }

        // Required: BloomSegment.Add() is not safe for concurrent writers.
        using var _w = _writeLock.EnterScope();

        // Optional but recommended: re-check under the write lock to reduce duplicates under concurrency.
        // This preserves your “Count only on first insert” intent much better.
        if (MightContain(h1))
        {
            _skipped.Inc();
            return;
        }

        _total.Inc();
        _current.Add(h1);

        if (_current.IsFull)
            Rotate_NoLockOrderViolation();
    }

    private void Rotate_NoLockOrderViolation()
    {
        // Always acquire locks in the same order everywhere: writeLock -> segmentsLock
        using var _s = _segmentsLock.EnterScope();

        _current.Seal();
        _current = CreateNewSegment_NoLock();
        PublishSnapshot_NoLock();
    }

    private BloomSegment CreateNewSegment_NoLock()
    {
        string path = Path.Combine(_directory, $"segment_{DateTime.UtcNow.Ticks}.bloom");

        var seg = new BloomSegment(path, _segmentCapacity, _bitsPerKey, createNew: true);

        _segments.Insert(0, seg);
        return seg;
    }

    private void LoadExistingSegments_NoLock()
    {
        foreach (var file in Directory.GetFiles(_directory, "*.bloom"))
        {
            var seg = new BloomSegment(file, capacity: 0, bitsPerKey: 0, createNew: false);

            _total.Inc(seg.Count);

            _segments.Add(seg);
            if (!seg.IsSealed)
                _current = seg;
        }

        _segments.Sort((a, b) => b.Count.CompareTo(a.Count));
    }

    private void PublishSnapshot_NoLock() => _snapshot = _segments.ToArray();

    public void Flush()
    {
        // Exclusive with writers: ensures no thread is in BloomSegment.Add while we flush.
        using var _w = _writeLock.EnterScope();
        using var _s = _segmentsLock.EnterScope();

        foreach (var seg in _snapshot)
            seg.Flush();
    }

    public void Dispose()
    {
        using var _w = _writeLock.EnterScope();
        using var _s = _segmentsLock.EnterScope();

        foreach (var seg in _snapshot)
            seg.Dispose();
    }
}
