// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Prometheus;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

/// <summary>
/// ChatGPT generated segmented block-bloom filter.
/// Used to have a null check.
/// </summary>
public sealed class SegmentedBloom : IDisposable
{
    private Lock _lock = new Lock();
    readonly string _directory;
    readonly long _segmentCapacity;
    readonly int _bitsPerKey;

    readonly List<BloomSegment> _segments = new();
    BloomSegment _current;

    private static Gauge _bloomKeySize = Metrics.CreateGauge("segmented_bloom_counts", "", "type");
    private Gauge.Child _total = _bloomKeySize.WithLabels("total");
    private Gauge.Child _skipped = _bloomKeySize.WithLabels("skipped");

    private static Histogram _bloomTime = Metrics.CreateHistogram("segmented_bloom_read_time", "", new HistogramConfiguration()
    {
        LabelNames = ["hitmiss"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 9, 3),
    });

    private Histogram.Child _bloomHitTime = _bloomTime.WithLabels("hit");
    private Histogram.Child _bloomMissTime = _bloomTime.WithLabels("miss");

    public SegmentedBloom(
        string directory,
        long segmentCapacity,
        int bitsPerKey)
    {
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        _bitsPerKey = bitsPerKey;

        Directory.CreateDirectory(directory);
        LoadExistingSegments();

        if (_current == null)
            _current = CreateNewSegment();
    }

    public bool MightContain(ulong h1)
    {
        long sw = Stopwatch.GetTimestamp();
        foreach (var seg in _segments)
            if (seg.MightContain(h1))
            {
                _bloomHitTime.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }
        _bloomMissTime.Observe(Stopwatch.GetTimestamp() - sw);
        return false;
    }

    public void Add(ulong h1)
    {
        if (MightContain(h1))
        {
            _skipped.Inc();
            return;
        }

        using var _ = _lock.EnterScope();
        _total.Inc();
        _current.Add(h1);
        if (_current.IsFull)
            Rotate();
    }

    void Rotate()
    {
        _current.Seal();
        _current = CreateNewSegment();
    }

    BloomSegment CreateNewSegment()
    {
        string path = Path.Combine(
            _directory,
            $"segment_{DateTime.UtcNow.Ticks}.bloom");

        var seg = new BloomSegment(
            path,
            _segmentCapacity,
            _bitsPerKey,
            createNew: true);

        _segments.Insert(0, seg);
        return seg;
    }

    void LoadExistingSegments()
    {
        foreach (var file in Directory.GetFiles(_directory, "*.bloom"))
        {
            var seg = new BloomSegment(
                file,
                capacity: 0,
                bitsPerKey: 0,
                createNew: false);

            _total.Inc(seg.Count);

            _segments.Add(seg);
            if (!seg.IsSealed)
                _current = seg;
        }

        _segments.Sort((a, b) => b.Count.CompareTo(a.Count));
    }

    public void Dispose()
    {
        foreach (var seg in _segments)
            seg.Dispose();
    }

    public void Flush()
    {
        using var _ = _lock.EnterScope();
        foreach (var seg in _segments)
            seg.Flush();
    }
}
