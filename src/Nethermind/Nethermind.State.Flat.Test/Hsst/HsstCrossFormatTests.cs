// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Parameterized cross-format invariant test: the same 100-entry corpus of random
/// 8-byte keys → 8-byte values must round-trip identically through every HSST format
/// that supports 8-byte keys. Add (build), Get (exact-seek) and Enumerate must all
/// agree on the corpus regardless of the on-disk layout. Catches the LE-stored
/// merge / encoding family of bugs by exercising both BE-stored and LE-stored
/// PackedArray side-by-side with the lex-bytes BTree format.
/// </summary>
[TestFixture]
public class HsstCrossFormatTests
{
    public enum Format { BTree, PackedArrayBe, PackedArrayLe }

    private const int KeySize = 8;
    private const int ValueSize = 8;
    private const int Count = 100;

    [TestCase(Format.BTree)]
    [TestCase(Format.PackedArrayBe)]
    [TestCase(Format.PackedArrayLe)]
    public void AddGetEnumerate_RoundTrip(Format format)
    {
        (byte[][] keys, byte[][] values) = MakeCorpus(seed: 42);
        byte[] data = Build(format, keys, values);

        SpanByteReader reader = new(data);

        for (int i = 0; i < keys.Length; i++)
        {
            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Assert.That(r.TrySeek(keys[i], out _), Is.True, $"missing key #{i} in {format}");
            Bound vb = r.GetBound();
            byte[] got = data.AsSpan().Slice((int)vb.Offset, (int)vb.Length).ToArray();
            Assert.That(got, Is.EqualTo(values[i]), $"value mismatch at #{i} in {format}");
        }

        byte[] missing = new byte[KeySize];
        Array.Fill(missing, (byte)0xab);
        if (!keys.Any(k => k.AsSpan().SequenceEqual(missing)))
        {
            using HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Assert.That(r.TrySeek(missing, out _), Is.False, $"unexpected hit for unstored key in {format}");
        }

        List<(byte[] Key, byte[] Value)> enumerated = [];
        Span<byte> keyScratch = stackalloc byte[KeySize];
        using (HsstRefEnumerator<SpanByteReader, NoOpPin> e = new(in reader, new Bound(0, data.Length)))
        {
            while (e.MoveNext())
            {
                ReadOnlySpan<byte> logicalKey = e.CopyCurrentLogicalKey(keyScratch);
                Bound vb = e.Current.ValueBound;
                enumerated.Add((
                    logicalKey.ToArray(),
                    data.AsSpan().Slice((int)vb.Offset, (int)vb.Length).ToArray()));
            }
        }

        Assert.That(enumerated.Count, Is.EqualTo(Count), $"enumerated count mismatch in {format}");
        for (int i = 0; i < Count; i++)
        {
            Assert.That(enumerated[i].Key, Is.EqualTo(keys[i]), $"enumerated key #{i} mismatch in {format}");
            Assert.That(enumerated[i].Value, Is.EqualTo(values[i]), $"enumerated value #{i} mismatch in {format}");
        }
    }

    private static byte[] Build(Format format, byte[][] keys, byte[][] values)
    {
        using PooledByteBufferWriter pooled = new(64 * 1024);
        switch (format)
        {
            case Format.BTree:
            {
                HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> b
                    = new(ref pooled.GetWriter(), new HsstBTreeOptions { MinSeparatorLength = KeySize });
                try
                {
                    for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                    b.Build();
                }
                finally { b.Dispose(); }
                break;
            }
            case Format.PackedArrayBe:
            case Format.PackedArrayLe:
            {
                HsstPackedArrayBuilder<PooledByteBufferWriter.Writer> b = new(
                    ref pooled.GetWriter(),
                    keySize: KeySize,
                    valueSize: ValueSize,
                    expectedKeyCount: keys.Length,
                    isLittleEndian: format == Format.PackedArrayLe);
                try
                {
                    for (int i = 0; i < keys.Length; i++) b.Add(keys[i], values[i]);
                    b.Build();
                }
                finally { b.Dispose(); }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return pooled.WrittenSpan.ToArray();
    }

    private static (byte[][] Keys, byte[][] Values) MakeCorpus(int seed)
    {
        Random rng = new(seed);
        HashSet<string> seen = [];
        List<byte[]> ks = new(Count);
        while (ks.Count < Count)
        {
            byte[] k = new byte[KeySize];
            rng.NextBytes(k);
            if (seen.Add(Convert.ToHexString(k))) ks.Add(k);
        }
        ks.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));

        byte[][] vs = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            byte[] v = new byte[ValueSize];
            rng.NextBytes(v);
            vs[i] = v;
        }
        return (ks.ToArray(), vs);
    }
}
