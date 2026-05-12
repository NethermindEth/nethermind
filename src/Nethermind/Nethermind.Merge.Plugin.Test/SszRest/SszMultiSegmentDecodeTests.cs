// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Merge.Plugin.Test.SszRest;

/// <summary>
/// Exercises the multi-segment branch of every <c>Decode(ReadOnlySequence&lt;byte&gt;, ...)</c>
/// path. Production traffic from Kestrel arrives in 4 KB pooled blocks, so any blob-bearing
/// NewPayload runs through the multi-segment consolidation in the generator's emit and the
/// stack-copy fallback in <c>SszLib</c>'s primitive sequence decoders. Single-segment input
/// (covered by <see cref="SszCodecTests"/>) hits the zero-copy fast path; this fixture covers
/// the path that gets pool-rented + copied.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class SszMultiSegmentDecodeTests
{
    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    /// <summary>
    /// Splits <paramref name="data"/> into segments of size <paramref name="segmentSize"/>.
    /// segmentSize=1 produces the worst-case "every byte crosses a boundary" sequence.
    /// </summary>
    private static ReadOnlySequence<byte> Multi(byte[] data, int segmentSize)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;
        if (segmentSize <= 0 || data.Length <= segmentSize)
            return new ReadOnlySequence<byte>(data);

        BufferSegment first = new(data.AsMemory(0, segmentSize));
        BufferSegment last = first;
        for (int offset = segmentSize; offset < data.Length; offset += segmentSize)
        {
            int len = Math.Min(segmentSize, data.Length - offset);
            last = last.Append(data.AsMemory(offset, len));
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static byte[] EncodeBytes<T>(T value, Func<T, IBufferWriter<byte>, int> encode)
    {
        ArrayBufferWriter<byte> w = new();
        encode(value, w);
        return w.WrittenSpan.ToArray();
    }

    // Segment sizes chosen to cover: every-byte-boundary worst case (1), small odd
    // boundaries that misalign the SSZ 4-byte fixed header (3, 7), and the realistic
    // Kestrel pool block size (4096).
    private static readonly int[] SegmentSizes = [1, 3, 7, 4096];

    [TestCaseSource(nameof(SegmentSizes))]
    public void Capabilities_decodes_correctly_across_segments(int segSize)
    {
        string[] caps = ["POST /engine/v5/payloads", "POST /engine/v4/forkchoice", "GET /engine/v6/payloads/{id}"];
        byte[] encoded = EncodeBytes<IReadOnlyList<string>>(caps, SszCodec.EncodeCapabilitiesResponse);

        string[] decoded = SszCodec.DecodeCapabilitiesRequest(Multi(encoded, segSize));

        Assert.That(decoded, Is.EqualTo(caps));
    }

    [TestCaseSource(nameof(SegmentSizes))]
    public void GetBlobsRequest_decodes_correctly_across_segments(int segSize)
    {
        // Five hashes in a list — exercises the variable-length list-of-Bytes32 path
        // that lands a 4-byte offset right at the front and 32-byte chunks after.
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE];
        byte[] encoded = new byte[4 + hashes.Length * 32];
        encoded[0] = 0x04;
        for (int i = 0; i < hashes.Length; i++)
            Buffer.BlockCopy(hashes[i].Bytes.ToArray(), 0, encoded, 4 + i * 32, 32);

        byte[][] decoded = SszCodec.DecodeGetBlobsRequest(Multi(encoded, segSize));

        Assert.That(decoded.Length, Is.EqualTo(hashes.Length));
        for (int i = 0; i < hashes.Length; i++)
            Assert.That(decoded[i], Is.EqualTo(hashes[i].Bytes.ToArray()));
    }

    [TestCaseSource(nameof(SegmentSizes))]
    public void GetPayloadBodiesByRange_decodes_correctly_across_segments(int segSize)
    {
        // Two ulongs back-to-back — tests the cross-segment 8-byte primitive read in
        // SszLib.Decode(ReadOnlySequence<byte>, out ulong) when segSize < 8.
        const ulong startVal = 0x0102_0304_0506_0708ul;
        const ulong countVal = 0x1112_1314_1516_1718ul;
        byte[] encoded = new byte[16];
        BitConverter.TryWriteBytes(encoded.AsSpan(0, 8), startVal);
        BitConverter.TryWriteBytes(encoded.AsSpan(8, 8), countVal);

        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(Multi(encoded, segSize));

        Assert.That(start, Is.EqualTo((long)startVal));
        Assert.That(count, Is.EqualTo((long)countVal));
    }

    [TestCaseSource(nameof(SegmentSizes))]
    public void NewPayloadV3RequestWire_decodes_correctly_across_segments(int segSize)
    {
        // The most schema-rich path: nested struct (SszExecutionPayloadV3 with
        // variable transactions/withdrawals lists) + a list of fixed Hash256 + a
        // trailing fixed Hash256. Exercises offset reads, recursive Decode, and
        // multi-segment primitive reads in roughly that order.
        NewPayloadV3RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(SszTestData.MakeV3Payload()),
            ExpectedBlobVersionedHashes = [TestItem.KeccakA, TestItem.KeccakB],
            ParentBeaconBlockRoot = TestItem.KeccakC,
        };
        byte[] encoded = NewPayloadV3RequestWire.Encode(wire);

        NewPayloadV3RequestWire.Decode(Multi(encoded, segSize), out NewPayloadV3RequestWire decoded);

        Assert.That(decoded.ParentBeaconBlockRoot, Is.EqualTo(TestItem.KeccakC));
        Assert.That(decoded.ExpectedBlobVersionedHashes, Is.Not.Null);
        Assert.That(decoded.ExpectedBlobVersionedHashes!.Length, Is.EqualTo(2));
        Assert.That(decoded.ExpectedBlobVersionedHashes![0], Is.EqualTo(TestItem.KeccakA));
        Assert.That(decoded.ExpectedBlobVersionedHashes[1], Is.EqualTo(TestItem.KeccakB));
        ExecutionPayloadV3 payload = decoded.ExecutionPayload.Unwrap();
        Assert.That(payload.BlockNumber, Is.EqualTo(100));
        Assert.That(payload.BlockHash, Is.EqualTo(TestItem.KeccakE));
    }

    private delegate void SequenceDecode<T>(ReadOnlySequence<byte> data, out T result);

    /// <summary>
    /// Splits <paramref name="data"/> at <paramref name="splitAt"/>, asserts the resulting
    /// sequence is genuinely multi-segment, and verifies <paramref name="decode"/> recovers
    /// <paramref name="expected"/>. Single helper for every <c>SszLib.Decode(seq, out X)</c>
    /// primitive overload — they all share the stack-copy fallback in <c>ToContiguous</c>.
    /// </summary>
    private static void AssertDecodesAcrossSegmentBoundary<T>(
        byte[] data, int splitAt, T expected, SequenceDecode<T> decode)
    {
        ReadOnlySequence<byte> seq = Multi(data, splitAt);
        Assert.That(seq.IsSingleSegment, Is.False, "test setup must produce multi-segment input");

        decode(seq, out T value);

        Assert.That(value, Is.EqualTo(expected));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void SszLib_uint_decodes_with_segment_boundary_inside_primitive(int splitAt) =>
        AssertDecodesAcrossSegmentBoundary<uint>(
            [0x78, 0x56, 0x34, 0x12], splitAt, 0x12345678u, SszLib.Decode);

    [TestCase(1)]
    [TestCase(7)]
    public void SszLib_ulong_decodes_with_segment_boundary_inside_primitive(int splitAt) =>
        AssertDecodesAcrossSegmentBoundary<ulong>(
            [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01], splitAt, 0x0102_0304_0506_0708ul, SszLib.Decode);

    [Test]
    public void SszLib_UInt256_decodes_with_segments_at_every_byte_boundary()
    {
        UInt256 expected = UInt256.Parse("0xdeadbeefcafebabe0000111122223333444455556666777788889999aaaabbbb");
        byte[] data = new byte[32];
        expected.ToLittleEndian(data);

        AssertDecodesAcrossSegmentBoundary<UInt256>(data, splitAt: 1, expected, SszLib.Decode);
    }
}
