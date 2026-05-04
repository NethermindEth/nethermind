// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark targeting the HSST seek hot path
/// (<see cref="HsstReader{TReader, TPin}.TrySeek"/> +
/// <see cref="Nethermind.State.Flat.BSearchIndex.BSearchIndexReader"/> binary search).
///
/// Builds an HSST in memory once with fixed-width keys so the index nodes use Uniform
/// (KeyType=1) layout, then measures Seek_Hit/Seek_Miss across a range of key widths.
/// Use this to validate SIMD/dispatch-hoist changes in <c>BSearchIndexReader</c>.
///
/// Recommended invocation (from CLAUDE.md — <c>--quick</c> is broken in this repo):
/// <c>--launchCount 1 --warmupCount 3 --iterationCount 3 --filter '*HsstReaderBenchmark*'</c>.
/// </summary>
[MemoryDiagnoser]
public class HsstReaderBenchmark
{
    private byte[] _hsst = null!;
    private byte[][] _hitKeys = null!;
    private byte[][] _missKeys = null!;
    private int _index;

    [Params(4, 8, 32, 100)]
    public int KeyLen { get; set; }

    [Params(50_000)]
    public int EntryCount { get; set; }

    private const int LookupBatch = 1024;

    [GlobalSetup]
    public void Setup()
    {
        // Generate sorted unique keys with deterministic content; all the same width so
        // index nodes use Uniform (KeyType=1) and exercise the SIMD fast path when
        // KeyLen is small enough.
        byte[][] keys = new byte[EntryCount][];
        for (int i = 0; i < EntryCount; i++)
        {
            byte[] k = new byte[KeyLen];
            // Encode i as big-endian into the first 8 bytes so keys sort correctly.
            BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(0, Math.Min(8, KeyLen)), (ulong)(i * 2)); // even values → odd values are misses
            keys[i] = k;
        }

        using PooledByteBufferWriter pooled = new(64 * 1024 * 1024);
        HsstBuilder<PooledByteBufferWriter.Writer> builder = new(ref pooled.GetWriter());
        try
        {
            Span<byte> value = stackalloc byte[8];
            for (int i = 0; i < EntryCount; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(value, (ulong)i);
                builder.Add(keys[i], value);
            }
            builder.Build();
            _hsst = pooled.WrittenSpan.ToArray();
        }
        finally
        {
            builder.Dispose();
        }

        // Hit keys: shuffled subset of stored keys.
        Random rng = new(0xC0FFEE);
        _hitKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
        {
            _hitKeys[i] = keys[rng.Next(EntryCount)];
        }

        // Miss keys: odd-encoded values (no overlap with stored even-encoded keys).
        _missKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
        {
            byte[] k = new byte[KeyLen];
            ulong v = (ulong)(rng.Next(EntryCount) * 2 + 1);
            BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(0, Math.Min(8, KeyLen)), v);
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
        _index++;
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
        _index++;
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
        _index++;
        return acc;
    }
}
