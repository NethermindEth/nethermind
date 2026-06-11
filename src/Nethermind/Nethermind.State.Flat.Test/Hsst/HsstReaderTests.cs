// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// Reader-specific tests that don't generalize across HSST formats: BTree's internal
/// separator routing (a layout invariant) and the <see cref="HsstReader{TReader,TPin}"/>
/// copy/rent fallback path exercised by a non-span-backed <see cref="IHsstByteReader{TPin}"/>.
/// Generic round-trip coverage lives in <see cref="HsstCrossFormatTests"/>.
/// </summary>
[TestFixture]
public class HsstReaderTests
{
    /// <summary>
    /// Regression for the BTree internal-node boundary separator bug.
    /// </summary>
    /// <remarks>
    /// Every value is one full page, so each entry lands in its own page-local leaf and the
    /// [0xA9,0xFF,*] and [0xAB,0xCD,*] families end up in separate leaves regardless of the
    /// builder's page-packing heuristics. The natural separator between the two families is
    /// LCP([0xA9,0xFF,…], [0xAB,0xCD,…]) + 1 = 1 byte (= [0xAB]).
    ///
    /// Search key K = [0xAB, 0x00, 0x00] matches that truncated separator (0xAB) and would
    /// route to the [0xAB,0xCD,*] side — where it falls before every key (0xAB &lt; 0xABCD…)
    /// and TryGetFloor would have returned false, missing the actual floor in the
    /// [0xA9,0xFF,*] family. With the separator routing fixed, the parent's floor compare
    /// detects K &lt; S and routes K left, returning the last [0xA9,0xFF,*] entry as the floor.
    /// </remarks>
    [Test]
    public void TrySeekFloor_AcrossTruncatedSeparatorBoundary_RoutesCorrectly()
    {
        // One-page values force each entry into its own leaf (an entry larger than a page
        // can never share one), guaranteeing the inter-family leaf boundary the bug needs.
        static byte[] PageValue(int marker)
        {
            byte[] v = new byte[PageLayout.PageSize];
            v[0] = (byte)marker;
            return v;
        }

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            for (int i = 0; i < 32; i++)
                builder.Add([0xA9, 0xFF, (byte)i], PageValue(0xA0 + i));
            for (int i = 0; i < 32; i++)
                builder.Add([0xAB, 0xCD, (byte)i], PageValue(0xB0 + i));
        });

        // A single B-tree node is capped at 64 KiB, so a blob this large can only be a
        // multi-leaf tree — the inter-family separator routing is genuinely exercised.
        Assert.That(data.Length, Is.GreaterThan(64 * 1024));

        Assert.That(HsstTestUtil.TryGetFloor(data, [0xAB, 0x00, 0x00], out byte[] floorValue), Is.True,
            "Floor of [0xAB, 0x00, 0x00] should resolve to the last [0xA9, 0xFF, *] entry");
        // Last [0xA9, 0xFF, *] entry is [0xA9, 0xFF, 0x1F]; its page value's first byte is 0xA0 + 31 = 0xBF.
        Assert.That(floorValue.Length, Is.EqualTo(PageLayout.PageSize),
            "Floor must be the last [0xA9, 0xFF, *] entry's value, not a [0xAB, 0xCD, *] entry");
        Assert.That(floorValue[0], Is.EqualTo((byte)0xBF));
    }

    /// <summary>
    /// Forces the copy/rent fallback path inside <see cref="HsstReader{TReader,TPin}.TryLoadNode"/>:
    /// every <see cref="IHsstByteReader{TPin}.PinBuffer"/> rents a pooled buffer and copies into it,
    /// instead of returning a zero-copy slice. Mirrors what a paged or stream-backed reader
    /// would do when a requested range can't be served as a contiguous span.
    /// </summary>
    private struct CopyOnlyByteReader(byte[] data) : IHsstByteReader<PooledArrayPin>
    {
        private readonly byte[] _data = data;

        public readonly long Length => _data.Length;

        public readonly bool TryRead(long offset, Span<byte> output)
        {
            if ((ulong)offset > (ulong)(_data.Length - output.Length)) return false;
            _data.AsSpan((int)offset, output.Length).CopyTo(output);
            return true;
        }

        public readonly PooledArrayPin PinBuffer(long offset, long size)
        {
            if ((ulong)offset + (ulong)size > (ulong)_data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            PooledArrayPin pin = PooledArrayPin.Rent((int)size, out Span<byte> rented);
            _data.AsSpan((int)offset, (int)size).CopyTo(rented);
            return pin;
        }

        public readonly void Prefetch(long offset) { }
    }

    [TestCase(1)]
    [TestCase(64)]
    [TestCase(200)]
    [TestCase(1000)]
    public void CopyOnlyReader_TrySeek_ParityWithSpanReader(int count)
    {
        (string Key, string Value)[] entries = new (string, string)[count];
        for (int i = 0; i < count; i++)
            entries[i] = ($"key_{i:D6}", $"val_{i:D6}");

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer> builder) =>
        {
            foreach ((string key, string value) in entries)
                builder.Add(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        });

        CopyOnlyByteReader reader = new(data);
        using HsstReader<CopyOnlyByteReader, PooledArrayPin> r = new(in reader);
        Bound root = r.GetBound();

        foreach ((string key, string value) in entries)
        {
            r.SetBound(root);
            Assert.That(r.TrySeek(Encoding.UTF8.GetBytes(key), out _), Is.True, $"Key {key} not found");
            Span<byte> buf = new byte[r.GetBound().Length];
            r.GetValue(buf);
            Assert.That(Encoding.UTF8.GetString(buf), Is.EqualTo(value), $"Value mismatch for {key}");
        }

        // Floor for a key before all entries returns false even via the copy path.
        r.SetBound(root);
        Assert.That(r.TrySeek(""u8, out _), Is.False);
    }
}
