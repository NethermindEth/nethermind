// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark targeting the HSST seek hot path
/// (<see cref="HsstReader{TReader, TPin}.TrySeek"/> +
/// <see cref="Nethermind.State.Flat.BSearchIndex.BSearchIndexReader"/> binary search).
///
/// Uses 32-byte uniformly-random keys with <c>minSeparatorLength=4</c> to mirror the
/// production state-tree shape (UnL-4 dominant). Sweeps over leaf size and
/// SIMD-on/off so we can compare scalar binary search against the SIMD floor scan.
///
/// Recommended invocation: <c>--filter '*HsstReaderBenchmark*' --launchCount 1
/// --warmupCount 3 --iterationCount 5</c>.
/// </summary>
[MemoryDiagnoser]
public class HsstReaderBenchmark
{
    private byte[] _hsst = null!;
    private byte[][] _hitKeys = null!;
    private byte[][] _missKeys = null!;

    [Params(8_000_000)]
    public int EntryCount { get; set; }

    [Params(64, 128, 256, 512, 1024)]
    public int MaxLeafEntries { get; set; }

    [Params(false, true)]
    public bool SimdEnabled { get; set; }

    [Params(false, true)]
    public bool BranchlessSearch { get; set; }

    private const int KeyLen = 32;
    private const int MinSep = 4;
    private const int LookupBatch = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        BSearchIndexReaderSimd.Enabled = SimdEnabled;
        BSearchIndexReader.BranchlessSearch = BranchlessSearch;

        Random rng = new(42);
        byte[][] keys = new byte[EntryCount][];
        for (int i = 0; i < EntryCount; i++)
        {
            byte[] k = new byte[KeyLen];
            rng.NextBytes(k);
            keys[i] = k;
        }
        Array.Sort(keys, static (a, b) => a.AsSpan().SequenceCompareTo(b));

        using PooledByteBufferWriter pooled = new(1024 * 1024 * 1024);
        HsstBuilder<PooledByteBufferWriter.Writer> builder = new(
            ref pooled.GetWriter(), new HsstBTreeOptions
            {
                MinSeparatorLength = MinSep,
                MaxLeafEntries = MaxLeafEntries,
            });
        try
        {
            Span<byte> value = stackalloc byte[8];
            for (int i = 0; i < EntryCount; i++)
            {
                for (int b = 0; b < 8; b++)
                    value[7 - b] = (byte)((ulong)i >> (b * 8));
                builder.Add(keys[i], value);
            }
            builder.Build();
            _hsst = pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }

        // Hit keys: shuffled subset of stored keys (so seeks land on existing entries).
        Random hitRng = new(0xC0FFEE);
        _hitKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
            _hitKeys[i] = keys[hitRng.Next(EntryCount)];

        // Miss keys: independently-drawn random 32-byte values; collision with stored keys
        // has probability ≈ EntryCount / 2^256, i.e. effectively zero.
        _missKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
        {
            byte[] k = new byte[KeyLen];
            hitRng.NextBytes(k);
            _missKeys[i] = k;
        }
    }

    [Benchmark]
    public long Seek_Hit()
    {
        long acc = 0;
        SpanByteReader reader = new(_hsst);
        for (int i = 0; i < LookupBatch; i++)
        {
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (r.TrySeek(_hitKeys[i], out _))
                acc += r.GetBound().Length;
        }
        return acc;
    }

    [Benchmark]
    public long Seek_Miss()
    {
        long acc = 0;
        SpanByteReader reader = new(_hsst);
        for (int i = 0; i < LookupBatch; i++)
        {
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (r.TrySeek(_missKeys[i], out _))
                acc += r.GetBound().Length;
        }
        return acc;
    }

    [Benchmark]
    public long SeekFloor_Miss()
    {
        long acc = 0;
        SpanByteReader reader = new(_hsst);
        for (int i = 0; i < LookupBatch; i++)
        {
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            if (r.TrySeekFloor(_missKeys[i], out _))
                acc += r.GetBound().Length;
        }
        return acc;
    }
}
