// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
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
    /// Builds two leaves:
    ///   leaf 0: 32 keys with prefix [0xA9, 0xFF]
    ///   leaf 1: 32 keys with prefix [0xAB, 0xCD]   ← leaf prefix length = 2
    /// Natural separator between them = LCP([0xA9,0xFF,…], [0xAB,0xCD,…]) + 1 = 1
    /// (= [0xAB]). The fix extends it to length 2 (= [0xAB, 0xCD]).
    ///
    /// Search key K = [0xAB, 0x00, 0x00] matches the OLD truncated separator (0xAB)
    /// and would route to leaf 1 — where it falls before every key (0xAB &lt; 0xABCD…)
    /// and TryGetFloor would have returned false, missing the actual floor in leaf 0.
    /// With the extended separator the parent's floor compare detects K &lt; S_1 and
    /// routes K to leaf 0, returning its last entry as the floor.
    /// </remarks>
    [Test]
    public void TrySeekFloor_AcrossTruncatedSeparatorBoundary_RoutesCorrectly()
    {
        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
        {
            for (int i = 0; i < 32; i++)
                builder.Add([0xA9, 0xFF, (byte)i], [(byte)(0xA0 + i)]);
            for (int i = 0; i < 32; i++)
                builder.Add([0xAB, 0xCD, (byte)i], [(byte)(0xB0 + i)]);
        }, maxLeafEntries: 32);

        Assert.That(HsstTestUtil.TryGetFloor(data, [0xAB, 0x00, 0x00], out byte[] floorValue), Is.True,
            "Floor of [0xAB, 0x00, 0x00] should resolve to the last entry of leaf 0");
        // Last entry of leaf 0 is [0xA9, 0xFF, 0x1F] with value [0xA0 + 31] = [0xBF].
        Assert.That(floorValue, Is.EqualTo(new byte[] { 0xBF }),
            "Floor should be the last entry of leaf 0, not a leaf-1 entry");
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

        byte[] data = HsstTestUtil.BuildToArray((ref HsstBTreeBuilder<PooledByteBufferWriter.Writer, PooledByteBufferWriter.WriterReader, NoOpPin> builder) =>
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
