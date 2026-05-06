// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.BSearchIndex;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Microbenchmark targeting the HSST seek hot path. Workload: 8M unique 4-byte
/// random keys, 8-byte values. Sweeps Flat / FlatSplitIndex / inline b-tree
/// (with three leaf-fanout sizes × {None, OneByte, TwoBytes} in-leaf hash probe).
/// Sizes are logged to /tmp/hsst-bench-sizes.csv during setup.
/// </summary>
[MemoryDiagnoser]
public class HsstReaderBenchmark
{
    public enum Scenario
    {
        Flat,
        BTree,
    }

    private byte[] _hsst = null!;
    private byte[][] _hitKeys = null!;
    private byte[][] _missKeys = null!;

    [Params(8_000_000)]
    public int EntryCount { get; set; }

    [Params(false)]
    public bool SimdEnabled { get; set; }

    [Params(Scenario.Flat, Scenario.BTree)]
    public Scenario Variant { get; set; }

    [Params(1024)]
    public int StrideBytes { get; set; }

    [Params(1024)]
    public int SummaryStrideBytes { get; set; }

    private const int KeyLen = 4;
    private const int ValLen = 8;
    private const int LookupBatch = 10_000;
    private const string SizeLogPath = "/tmp/hsst-bench-sizes.csv";

    [GlobalSetup]
    public void Setup()
    {
        BSearchIndexReaderSimd.Enabled = SimdEnabled;

        // Oversample to dedupe 4-byte random keys (~5K collisions in 8M draws on 32-bit space).
        Random rng = new(42);
        int sample = EntryCount + EntryCount / 64 + 1024;
        byte[][] raw = new byte[sample][];
        for (int i = 0; i < sample; i++)
        {
            byte[] k = new byte[KeyLen];
            rng.NextBytes(k);
            raw[i] = k;
        }
        Array.Sort(raw, static (a, b) => a.AsSpan().SequenceCompareTo(b));
        byte[][] keys = new byte[EntryCount][];
        int kept = 0;
        for (int i = 0; i < sample && kept < EntryCount; i++)
        {
            if (kept == 0 || !raw[i].AsSpan().SequenceEqual(keys[kept - 1]))
                keys[kept++] = raw[i];
        }
        if (kept < EntryCount)
            throw new InvalidOperationException($"Only {kept} unique keys after dedupe; raise sample size.");

        using PooledByteBufferWriter pooled = new(1024 * 1024 * 1024);
        switch (Variant)
        {
            case Scenario.Flat:
                BuildFlat(ref pooled.GetWriter(), keys, StrideBytes, SummaryStrideBytes);
                break;
            case Scenario.BTree:
                BuildBTree(ref pooled.GetWriter(), keys);
                break;
        }
        _hsst = pooled.WrittenSpan.ToArray();
        AppendSizeLog(Variant, StrideBytes, SummaryStrideBytes, _hsst.Length, EntryCount);
        DumpFlatLayout(Variant, StrideBytes, SummaryStrideBytes, _hsst);

        Random hitRng = new(0xC0FFEE);
        _hitKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
            _hitKeys[i] = keys[hitRng.Next(EntryCount)];

        _missKeys = new byte[LookupBatch][];
        for (int i = 0; i < LookupBatch; i++)
        {
            byte[] k = new byte[KeyLen];
            hitRng.NextBytes(k);
            _missKeys[i] = k;
        }
    }

    private static void BuildFlat(ref PooledByteBufferWriter.Writer writer, byte[][] keys, int strideBytes, int summaryStrideBytes)
    {
        // summaryStrideBytes ignored (HsstPackedArrayBuilder uses one stride for both levels).
        _ = summaryStrideBytes;
        HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> b = new(ref writer, KeyLen, ValLen,
            binaryIndexStrideBytes: strideBytes);
        try
        {
            Span<byte> v = stackalloc byte[ValLen];
            for (int i = 0; i < keys.Length; i++) { Encode(v, i); b.Add(keys[i], v); }
            b.Build();
        }
        finally { b.Dispose(); }
    }

    private static void BuildBTree(ref PooledByteBufferWriter.Writer writer, byte[][] keys)
    {
        HsstBuilder<PooledByteBufferWriter.Writer> b = new(ref writer, new HsstBTreeOptions
        {
            MaxLeafEntries = 256,
            MaxIntermediateEntries = 256,
        });
        try
        {
            Span<byte> v = stackalloc byte[ValLen];
            for (int i = 0; i < keys.Length; i++) { Encode(v, i); b.Add(keys[i], v); }
            b.Build();
        }
        finally { b.Dispose(); }
    }

    private static void Encode(Span<byte> v, int i)
    {
        for (int b = 0; b < ValLen; b++)
            v[ValLen - 1 - b] = (byte)((ulong)i >> (b * 8));
    }

    private static void AppendSizeLog(Scenario s, int stride, int summaryStride, int bytes, int entryCount)
    {
        try
        {
            File.AppendAllText(SizeLogPath,
                $"{s},stride={stride},summary={summaryStride},{bytes},{(double)bytes / entryCount:F3}\n");
        }
        catch { /* best-effort */ }
    }

    private static void DumpFlatLayout(Scenario s, int stride, int summaryStride, byte[] hsst)
    {
        try
        {
            // Footer layout (HsstFlatReader.TryReadLayout):
            //   ...[Metadata: keySize, valueSize, entryCount,
            //       entriesPerCk0Log2, recordsPerCkHigherLog2, depth,
            //       counts[0..depth)][MetadataLength: u8][IndexType: u8]
            int hsstEnd = hsst.Length;
            int metaLen = hsst[hsstEnd - 2];
            int metaStart = hsstEnd - 2 - metaLen;
            ReadOnlySpan<byte> meta = hsst.AsSpan(metaStart, metaLen);
            int p = 0;
            int keySize = Leb128.Read(meta, ref p);
            int valueSize = Leb128.Read(meta, ref p);
            int entryCount = Leb128.Read(meta, ref p);
            int e0log2 = Leb128.Read(meta, ref p);
            int rhlog2 = Leb128.Read(meta, ref p);
            int depth = Leb128.Read(meta, ref p);
            int[] counts = new int[depth];
            for (int i = 0; i < depth; i++) counts[i] = Leb128.Read(meta, ref p);

            string line = $"{s},stride={stride},summary={summaryStride},keySize={keySize},entries={entryCount}," +
                          $"entriesPerCk0={1 << e0log2},recordsPerCkHigher={1 << rhlog2},depth={depth},counts=[{string.Join(",", counts)}]";
            File.AppendAllText("/tmp/hsst-bench-layouts.csv", line + "\n");
        }
        catch { /* best-effort */ }
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
