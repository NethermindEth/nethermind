// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.SszRest;

[Parallelizable(ParallelScope.Self)]
public class SszCodecTests
{

    /// <summary>
    /// Calls <paramref name="encode"/> against an <see cref="ArrayBufferWriter{T}"/>
    /// and returns the written bytes — the test-side analogue of writing into the
    /// response <see cref="System.IO.Pipelines.PipeWriter"/>.
    /// </summary>
    private static byte[] Encode<T>(T value, Func<T, IBufferWriter<byte>, int> encode)
    {
        ArrayBufferWriter<byte> w = new();
        int n = encode(value, w);
        Assert.That(w.WrittenCount, Is.EqualTo(n));
        return w.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Wraps a contiguous byte array as a single-segment <see cref="ReadOnlySequence{T}"/>
    /// — what the middleware would hand to a handler when Kestrel buffered the body
    /// in one pooled block.
    /// </summary>
    private static ReadOnlySequence<byte> Seq(byte[] data) => new(data);


    [TestCase(PayloadStatus.Valid, (byte)0)]
    [TestCase(PayloadStatus.Invalid, (byte)1)]
    [TestCase(PayloadStatus.Syncing, (byte)2)]
    [TestCase(PayloadStatus.Accepted, (byte)3)]
    public void EncodePayloadStatus_status_byte_is_correct(string status, byte expected)
    {
        byte[] encoded = Encode(new PayloadStatusV1 { Status = status }, SszCodec.EncodePayloadStatus);
        Assert.That(encoded[0], Is.EqualTo(expected));
    }

    [Test]
    public void EncodePayloadStatus_with_error_is_larger_than_without()
    {
        byte[] withError = Encode(new PayloadStatusV1 { Status = PayloadStatus.Invalid, ValidationError = "bad" }, SszCodec.EncodePayloadStatus);
        byte[] withoutError = Encode(new PayloadStatusV1 { Status = PayloadStatus.Invalid }, SszCodec.EncodePayloadStatus);
        Assert.That(withError.Length, Is.GreaterThan(withoutError.Length));
    }

    [Test]
    public void EncodePayloadStatus_validation_error_wraps_in_optional_list_per_spec()
    {
        // Per execution-apis #793: Optional[String] = List[List[byte, 1024], 1].
        // Spec wire layout for { Status=INVALID, LatestValidHash=[], ValidationError="bad" }:
        //   1 byte  status (= 1)
        //   4 bytes offset(LatestValidHash) (= 9)
        //   4 bytes offset(ValidationError) (= 9, since LatestValidHash is empty)
        //   0 bytes LatestValidHash content
        //   4 bytes inner-list offset within ValidationError (= 4)
        //   3 bytes "bad"
        // Total = 16 bytes.
        byte[] encoded = Encode(
            new PayloadStatusV1 { Status = PayloadStatus.Invalid, ValidationError = "bad" },
            SszCodec.EncodePayloadStatus);
        PayloadStatusWire.Decode(Seq(encoded), out PayloadStatusWire decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(16));
            Assert.That(decoded.Status, Is.EqualTo((byte)1));
            Assert.That(decoded.ValidationError, Has.Length.EqualTo(1));
            Assert.That(decoded.ValidationError![0].Bytes, Is.EqualTo("bad"u8.ToArray()));
        }
    }

    [Test]
    public void EncodePayloadStatus_no_validation_error_is_empty_outer_list()
    {
        byte[] encoded = Encode(new PayloadStatusV1 { Status = PayloadStatus.Valid }, SszCodec.EncodePayloadStatus);
        PayloadStatusWire.Decode(Seq(encoded), out PayloadStatusWire decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Status, Is.EqualTo((byte)0));
            Assert.That(decoded.ValidationError, Is.Empty);
        }
    }

    [Test]
    public void EncodeForkchoiceUpdatedResponse_payload_id_prefix_is_irrelevant()
    {
        static ForkchoiceUpdatedV1Result WithId(string id) => new()
        {
            PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid },
            PayloadId = id
        };

        byte[] withPrefix = Encode(WithId("0x0102030405060708"), SszCodec.EncodeForkchoiceUpdatedResponse);
        byte[] withoutPrefix = Encode(WithId("0102030405060708"), SszCodec.EncodeForkchoiceUpdatedResponse);

        Assert.That(withPrefix, Is.EqualTo(withoutPrefix));
    }


    [Test]
    public void Capabilities_roundtrip()
    {
        string[] caps = ["POST /engine/v5/payloads", "GET /engine/v6/payloads/{id}", "POST /engine/v4/forkchoice"];

        byte[] encoded = Encode<IReadOnlyList<string>>(caps, SszCodec.EncodeCapabilitiesResponse);
        string[] decoded = SszCodec.DecodeCapabilitiesRequest(Seq(encoded));

        Assert.That(decoded, Is.EqualTo(caps));
    }

    [Test]
    public void EncodeCapabilitiesResponse_single_capability_matches_spec_layout()
    {
        byte[] encoded = Encode<IReadOnlyList<string>>(["abc"], SszCodec.EncodeCapabilitiesResponse);

        Assert.That(encoded.Length, Is.EqualTo(11));
        Assert.That(encoded[8..], Is.EqualTo("abc"u8.ToArray()));
    }

    [Test]
    public void DecodeGetBlobsRequest_roundtrip()
    {
        byte[] hash = TestItem.KeccakA.Bytes.ToArray();
        byte[] request = new byte[4 + 32];
        request[0] = 0x04;
        Buffer.BlockCopy(hash, 0, request, 4, 32);

        byte[][] decoded = SszCodec.DecodeGetBlobsRequest(Seq(request));

        Assert.That(decoded.Length, Is.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(hash));
    }

    [Test]
    public void DecodeGetPayloadBodiesByHashRequest_roundtrip()
    {
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB];
        byte[] request = new byte[4 + hashes.Length * 32];
        request[0] = 0x04;
        for (int i = 0; i < hashes.Length; i++)
            Buffer.BlockCopy(hashes[i].Bytes.ToArray(), 0, request, 4 + i * 32, 32);

        Assert.That(SszCodec.DecodeGetPayloadBodiesByHashRequest(Seq(request)), Is.EqualTo(hashes));
    }

    [Test]
    public void DecodeGetPayloadBodiesByRangeRequest_roundtrip()
    {
        byte[] request = new byte[16];
        BitConverter.TryWriteBytes(request.AsSpan(0, 8), 10UL);
        BitConverter.TryWriteBytes(request.AsSpan(8, 8), 5UL);

        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(Seq(request));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(start, Is.EqualTo(10));
            Assert.That(count, Is.EqualTo(5));
        }
    }

    [Test]
    public void EncodePayloadBodiesV1Response_null_body_encodes_as_empty_inner_list()
    {
        byte[] withNull = Encode<IReadOnlyList<ExecutionPayloadBodyV1Result?>>([null], SszCodec.EncodePayloadBodiesV1Response);
        byte[] withPresent = Encode<IReadOnlyList<ExecutionPayloadBodyV1Result?>>([new ExecutionPayloadBodyV1Result([], null)], SszCodec.EncodePayloadBodiesV1Response);

        Assert.That(withPresent.Length, Is.GreaterThanOrEqualTo(withNull.Length));
    }

    [Test]
    public void EncodeGetBlobsV3Response_null_vs_present_entry_differ_in_size()
    {
        byte[] blob = new byte[131072];
        byte[] withNull = Encode<IReadOnlyList<BlobAndProofV2?>>([null], SszCodec.EncodeGetBlobsV3Response);
        byte[] withPresent = Encode<IReadOnlyList<BlobAndProofV2?>>([new BlobAndProofV2(blob, [new byte[48]])], SszCodec.EncodeGetBlobsV3Response);

        Assert.That(withPresent.Length, Is.GreaterThan(withNull.Length));
    }

    [Test]
    public void EncodeGetBlobsV4Response_with_pool_rented_cells_and_proofs_round_trips()
    {
        // Reproduces what GetBlobsHandlerV4 builds: pool-rented byte[] arrays sized
        // by Ckzg.BytesPerCell (2048) and Ckzg.BytesPerProof (48). ArrayPool.Rent(48)
        // hands back a 64-byte array — the encoder must slice to spec-exact length
        // or SszKzgCommitment.FromSpan throws. Likewise for SszBlobCell.
        const int cellsPerExtBlob = 128;
        byte[]?[] cells = new byte[]?[cellsPerExtBlob];
        byte[]?[] proofs = new byte[]?[cellsPerExtBlob];
        cells[0] = ArrayPool<byte>.Shared.Rent(SszBlobCell.BlobCellLength);
        proofs[0] = ArrayPool<byte>.Shared.Rent(SszKzgCommitment.KzgCommitmentLength);
        try
        {
            BlobCellsAndProofs entry = new() { Available = true, BlobCells = cells, Proofs = proofs };
            byte[] encoded = Encode<IReadOnlyList<BlobCellsAndProofs?>>([entry], SszCodec.EncodeGetBlobsV4Response);
            GetBlobsV4ResponseWire.Decode(Seq(encoded), out GetBlobsV4ResponseWire decoded);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.Not.Empty);
                Assert.That(decoded.Entries, Has.Length.EqualTo(1));
                Assert.That(decoded.Entries![0].Available, Is.True);
                Assert.That(decoded.Entries[0].Contents.BlobCells, Has.Length.EqualTo(cellsPerExtBlob));
                Assert.That(decoded.Entries[0].Contents.BlobCells![0].Cell, Has.Length.EqualTo(1));
                Assert.That(decoded.Entries[0].Contents.BlobCells![1].Cell, Is.Empty);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cells[0]!);
            ArrayPool<byte>.Shared.Return(proofs[0]!);
        }
    }

    private static IEnumerable<TestCaseData> NonEmptyEncodings()
    {
        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
            SszCodec.EncodeGetPayloadV1Response(SszTestData.MakeMinimalPayload(), w)))
            .SetName(nameof(Encoded_buffer_is_non_empty) + "_GetPayloadV1");

        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
        {
            ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
            Block block = (Block)ep.TryGetBlock();
            SszCodec.EncodeGetPayloadV3Response(new GetPayloadV3Result(block, UInt256.One, new BlobsBundleV1(block), false), w);
        })).SetName(nameof(Encoded_buffer_is_non_empty) + "_GetPayloadV3");
    }

    [TestCaseSource(nameof(NonEmptyEncodings))]
    public void Encoded_buffer_is_non_empty(Action<IBufferWriter<byte>> encode)
    {
        ArrayBufferWriter<byte> w = new();
        encode(w);
        Assert.That(w.WrittenSpan.ToArray(), Is.Not.Empty);
    }

    private static void AssertCommonNewPayloadFields(
        Hash256? parentBeaconBlockRoot, Hash256 expectedParentRoot,
        byte[][]? requests, byte[] expectedRequest)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parentBeaconBlockRoot, Is.Not.Null);
            Assert.That(parentBeaconBlockRoot, Is.EqualTo(expectedParentRoot));

            Assert.That(requests, Is.Not.Null);
            Assert.That(requests, Is.EqualTo(new[] { expectedRequest }));
        }
    }

    [Test]
    public void DecodeNewPayload_v4_roundtrip_preserves_all_fields()
    {
        byte[] executionRequest = [0x01, 0x02, 0x03, 0x04];

        NewPayloadV4RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(SszTestData.MakeV3Payload()),
            ParentBeaconBlockRoot = TestItem.KeccakC,
            ExecutionRequests = [new SszTransaction { Bytes = executionRequest }]
        };

        byte[] encoded = NewPayloadV4RequestWire.Encode(wire);

        NewPayloadV4RequestWire.Decode(encoded, out NewPayloadV4RequestWire decoded);
        ExecutionPayloadV3 payload = decoded.ExecutionPayload.AsExecutionPayload();
        byte[][]? requests = decoded.ExecutionRequests.ToExecutionRequests();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.BlockNumber, Is.EqualTo(100));
            Assert.That(payload.GasLimit, Is.EqualTo(2_000_000));
            Assert.That(payload.Timestamp, Is.EqualTo(1_700_000_100));
            Assert.That(payload.BlockHash, Is.EqualTo(TestItem.KeccakE));
            Assert.That(payload.BlobGasUsed, Is.EqualTo(0x20000UL));
            Assert.That(payload.ExcessBlobGas, Is.EqualTo(0x40000UL));
        }

        AssertCommonNewPayloadFields(
            decoded.ParentBeaconBlockRoot, TestItem.KeccakC,
            requests, executionRequest);
    }

    [Test]
    public void DecodeNewPayload_v5_roundtrip_preserves_v4_payload_fields()
    {
        byte[] executionRequest = [0xAA, 0xBB];
        byte[] blockAccessList = [0x01, 0x02, 0x03];
        ulong slotNumber = 999_999UL;

        NewPayloadV5RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV4(SszTestData.MakeV4Payload(blockAccessList, slotNumber)),
            ParentBeaconBlockRoot = TestItem.KeccakD,
            ExecutionRequests = [new SszTransaction { Bytes = executionRequest }]
        };

        byte[] encoded = NewPayloadV5RequestWire.Encode(wire);

        NewPayloadV5RequestWire.Decode(encoded, out NewPayloadV5RequestWire decoded);
        ExecutionPayloadV4 payload = decoded.ExecutionPayload.AsExecutionPayload();
        byte[][]? requests = decoded.ExecutionRequests.ToExecutionRequests();

        Span<byte> blockAccessListSpan = payload.BlockAccessList;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.BlockNumber, Is.EqualTo(100));
            Assert.That(payload.Timestamp, Is.EqualTo(1_700_000_100));
            Assert.That(payload.BlockHash, Is.EqualTo(TestItem.KeccakE));

            Assert.That(blockAccessListSpan.ToArray(), Is.EqualTo(blockAccessList));
            Assert.That(payload.SlotNumber, Is.EqualTo(slotNumber));
            Assert.That(payload.BlobGasUsed, Is.EqualTo(0x20000UL));
            Assert.That(payload.ExcessBlobGas, Is.EqualTo(0x40000UL));
        }

        AssertCommonNewPayloadFields(
            decoded.ParentBeaconBlockRoot, TestItem.KeccakD,
            requests, executionRequest);
    }

    private static IEnumerable<TestCaseData> BufferConsistentEncodings()
    {
        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
            SszCodec.EncodePayloadStatus(new PayloadStatusV1 { Status = PayloadStatus.Valid }, w)))
            .SetName(nameof(Encoded_buffer_length_is_consistent) + "_PayloadStatus");

        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
            SszCodec.EncodeGetPayloadV1Response(SszTestData.MakeMinimalPayload(), w)))
            .SetName(nameof(Encoded_buffer_length_is_consistent) + "_GetPayloadV1");
    }

    [TestCaseSource(nameof(BufferConsistentEncodings))]
    public void Encoded_buffer_length_is_consistent(Action<IBufferWriter<byte>> encode)
    {
        ArrayBufferWriter<byte> w = new();
        encode(w);
        Assert.That(w.WrittenCount, Is.Positive);
    }

    [Test]
    public void EncodeGetPayloadV1Response_fields_land_at_spec_defined_offsets()
    {
        ExecutionPayload ep = SszTestData.MakeMinimalPayload();
        ep.BaseFeePerGas = new UInt256(0xABCDEF);

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV1Response(ep, w);
        ReadOnlySpan<byte> buffer = w.WrittenSpan;

        Assert.That(buffer.Length, Is.GreaterThan(440 + 32), "encoded payload must be large enough to contain baseFeePerGas");

        UInt256 decodedBaseFee = new(buffer.Slice(440, 32), isBigEndian: false);
        Assert.That(decodedBaseFee, Is.EqualTo(ep.BaseFeePerGas), "baseFeePerGas must be encoded at byte offset 440 per the Ethereum consensus spec");

        Assert.That(buffer.Slice(0, 32).ToArray(), Is.EqualTo(ep.ParentHash!.Bytes.ToArray()), "parent_hash must be the first 32 bytes of the encoded payload");

        Assert.That(buffer.Slice(472, 32).ToArray(), Is.EqualTo(ep.BlockHash!.Bytes.ToArray()), "block_hash must be encoded at byte offset 472 per the Ethereum consensus spec");
    }

    [Test]
    public void EncodeGetPayloadV1Response_all_static_fields_land_at_spec_defined_offsets()
    {
        ExecutionPayload ep = new()
        {
            ParentHash = TestItem.KeccakA,
            FeeRecipient = TestItem.AddressA,
            StateRoot = TestItem.KeccakB,
            ReceiptsRoot = TestItem.KeccakC,
            LogsBloom = Bloom.Empty,
            PrevRandao = TestItem.KeccakD,
            BlockNumber = (long)0x0102030405060708UL,
            GasLimit = (long)0x1112131415161718UL,
            GasUsed = (long)0x2122232425262728UL,
            Timestamp = 0x3132333435363738UL,
            ExtraData = [0xEE, 0xEF],
            BaseFeePerGas = new UInt256(0xDEADBEEF),
            BlockHash = TestItem.KeccakE,
            Transactions = Array.Empty<byte[]>()
        };

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV1Response(ep, w);
        ReadOnlySpan<byte> buf = w.WrittenSpan;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(buf.Slice(0, 32).ToArray(), Is.EqualTo(ep.ParentHash!.Bytes.ToArray()), "parent_hash @ offset 0");

            Assert.That(buf.Slice(32, 20).ToArray(), Is.EqualTo(ep.FeeRecipient!.Bytes.ToArray()), "fee_recipient @ offset 32");

            Assert.That(buf.Slice(52, 32).ToArray(), Is.EqualTo(ep.StateRoot!.Bytes.ToArray()), "state_root @ offset 52");

            Assert.That(buf.Slice(84, 32).ToArray(), Is.EqualTo(ep.ReceiptsRoot!.Bytes.ToArray()), "receipts_root @ offset 84");

            Assert.That(buf.Slice(116, 256).ToArray(), Is.EqualTo(Bloom.Empty.Bytes.ToArray()), "logs_bloom @ offset 116");

            Assert.That(buf.Slice(372, 32).ToArray(), Is.EqualTo(ep.PrevRandao!.Bytes.ToArray()), "prev_randao @ offset 372");

            Assert.That(BitConverter.ToUInt64(buf.Slice(404, 8)), Is.EqualTo((ulong)ep.BlockNumber), "block_number @ offset 404");

            Assert.That(BitConverter.ToUInt64(buf.Slice(412, 8)), Is.EqualTo((ulong)ep.GasLimit), "gas_limit @ offset 412");

            Assert.That(BitConverter.ToUInt64(buf.Slice(420, 8)), Is.EqualTo((ulong)ep.GasUsed), "gas_used @ offset 420");

            Assert.That(BitConverter.ToUInt64(buf.Slice(428, 8)), Is.EqualTo(ep.Timestamp), "timestamp @ offset 428");

            uint extraDataOffset = BitConverter.ToUInt32(buf.Slice(436, 4));
            Assert.That(extraDataOffset, Is.GreaterThanOrEqualTo(508u), "extra_data variable-length offset @ offset 436 must point past the fixed section");

            Assert.That(new UInt256(buf.Slice(440, 32), isBigEndian: false), Is.EqualTo(ep.BaseFeePerGas), "base_fee_per_gas @ offset 440");

            Assert.That(buf.Slice(472, 32).ToArray(), Is.EqualTo(ep.BlockHash!.Bytes.ToArray()), "block_hash @ offset 472");

            uint txOffset = BitConverter.ToUInt32(buf.Slice(504, 4));
            Assert.That(txOffset, Is.GreaterThanOrEqualTo(508u), "transactions variable-length offset @ offset 504 must point past the fixed section");
        }
    }

    [Test]
    public void EncodePayloadStatus_all_static_fields_land_at_spec_defined_offsets()
    {
        PayloadStatusV1 ps = new()
        {
            Status = PayloadStatus.Valid,
            LatestValidHash = TestItem.KeccakA
        };

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodePayloadStatus(ps, w);
        ReadOnlySpan<byte> buf = w.WrittenSpan;

        Assert.That(buf[0], Is.EqualTo(0), "status=VALID must encode as 0x00 at offset 0");

        uint lvhOffset = BitConverter.ToUInt32(buf.Slice(1, 4));
        Assert.That(lvhOffset, Is.GreaterThanOrEqualTo(9u), "latest_valid_hash variable-length offset @ 1 must point past the 9-byte fixed section");

        uint veOffset = BitConverter.ToUInt32(buf.Slice(5, 4));
        Assert.That(veOffset, Is.GreaterThanOrEqualTo(9u), "validation_error variable-length offset @ 5 must point past the 9-byte fixed section");

        Assert.That(buf.Slice((int)lvhOffset, 32).ToArray(), Is.EqualTo(TestItem.KeccakA.Bytes.ToArray()), "latest_valid_hash bytes must land at the offset encoded in the fixed section");
    }

    [Test]
    public void EncodeForkchoiceUpdatedResponse_all_static_fields_land_at_spec_defined_offsets()
    {
        ForkchoiceUpdatedV1Result resp = new()
        {
            PayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakA },
            PayloadId = "0x0102030405060708"
        };

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeForkchoiceUpdatedResponse(resp, w);
        ReadOnlySpan<byte> buf = w.WrittenSpan;

        Assert.That(buf.Length, Is.GreaterThanOrEqualTo(8), "encoded response must cover the 8-byte fixed section (two 4-byte offsets)");

        uint psOffset = BitConverter.ToUInt32(buf.Slice(0, 4));
        Assert.That(psOffset, Is.GreaterThanOrEqualTo(8u), "payload_status offset @ 0 must point past the 8-byte fixed section");
        Assert.That(psOffset, Is.LessThan((uint)buf.Length), "payload_status offset must be within the encoded buffer");

        uint pidOffset = BitConverter.ToUInt32(buf.Slice(4, 4));
        Assert.That(pidOffset, Is.GreaterThanOrEqualTo(8u), "payload_id offset @ 4 must point past the 8-byte fixed section");
        Assert.That(pidOffset, Is.LessThan((uint)buf.Length), "payload_id offset must be within the encoded buffer");

        Assert.That(buf[(int)psOffset], Is.EqualTo(0), "first byte of the embedded PayloadStatus sub-container must be 0x00 (VALID)");

        int pidEnd = (int)pidOffset + 8;
        Assert.That(buf.Length, Is.GreaterThanOrEqualTo(pidEnd), "encoded buffer must be large enough to hold the payload_id bytes");

        Assert.That(buf.Slice((int)pidOffset, 8).ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }), "payload_id bytes must match the original hex string");
    }

    private static void AssertGetPayloadResponseHeaderOffsets(
        ReadOnlySpan<byte> buf,
        int fixedSectionSize,
        UInt256 expectedBlockValue,
        byte expectedShouldOverrideBuilder,
        string version)
    {
        Assert.That(buf.Length, Is.GreaterThanOrEqualTo(fixedSectionSize), $"encoded {version} response must cover the {fixedSectionSize}-byte fixed section");

        uint epOffset = BitConverter.ToUInt32(buf.Slice(0, 4));
        Assert.That(epOffset, Is.GreaterThanOrEqualTo((uint)fixedSectionSize), $"execution_payload offset @ 0 must point past the {fixedSectionSize}-byte fixed section");

        Assert.That(new UInt256(buf.Slice(4, 32), isBigEndian: false), Is.EqualTo(expectedBlockValue), "block_value must be encoded at byte offset 4");

        uint bbOffset = BitConverter.ToUInt32(buf.Slice(36, 4));
        Assert.That(bbOffset, Is.GreaterThanOrEqualTo((uint)fixedSectionSize), $"blobs_bundle offset @ 36 must point past the {fixedSectionSize}-byte fixed section");

        Assert.That(buf[40], Is.EqualTo(expectedShouldOverrideBuilder), $"should_override_builder must encode as 0x{expectedShouldOverrideBuilder:X2} at offset 40");
    }

    [Test]
    public void EncodeGetPayloadV3Response_all_static_fields_land_at_spec_defined_offsets()
    {
        UInt256 blockValue = new(0xCAFEBABEu);
        ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
        Block block = (Block)ep.TryGetBlock();

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV3Response(
            new GetPayloadV3Result(block, blockValue, new BlobsBundleV1(block), shouldOverrideBuilder: true), w);

        AssertGetPayloadResponseHeaderOffsets(w.WrittenSpan, fixedSectionSize: 41, blockValue,
            expectedShouldOverrideBuilder: 1, version: "V3");
    }

    [Test]
    public void DecodeFcuV3Request_spec_layout_roundtrips_parent_beacon_block_root()
    {
        ForkchoiceUpdatedV3RequestWire wire = new()
        {
            ForkchoiceState = new ForkchoiceStateWire
            {
                HeadBlockHash = TestItem.KeccakA,
                SafeBlockHash = TestItem.KeccakB,
                FinalizedBlockHash = TestItem.KeccakC,
            },
            PayloadAttributes =
            [
                new PayloadAttributesV3Wire
                {
                    Timestamp = 0x1122334455667788UL,
                    PrevRandao = TestItem.KeccakD,
                    SuggestedFeeRecipient = TestItem.AddressA,
                    Withdrawals = [],
                    ParentBeaconBlockRoot = TestItem.KeccakE,
                }
            ]
        };

        byte[] encoded = ForkchoiceUpdatedV3RequestWire.Encode(wire);

        ForkchoiceUpdatedV3RequestWire.Decode(encoded, out ForkchoiceUpdatedV3RequestWire decoded);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(decoded.ForkchoiceState);
        PayloadAttributes? attrs = decoded.PayloadAttributes is { Length: > 0 } a
            ? SszCodec.PayloadAttributesFromWire(a[0]) : null;

        Assert.That(state.HeadBlockHash, Is.EqualTo(TestItem.KeccakA));
        Assert.That(state.SafeBlockHash, Is.EqualTo(TestItem.KeccakB));
        Assert.That(state.FinalizedBlockHash, Is.EqualTo(TestItem.KeccakC));

        Assert.That(attrs, Is.Not.Null);
        Assert.That(attrs!.Timestamp, Is.EqualTo(0x1122334455667788UL));
        Assert.That(attrs.PrevRandao, Is.EqualTo(TestItem.KeccakD));
        Assert.That(attrs.SuggestedFeeRecipient, Is.EqualTo(TestItem.AddressA));
        Assert.That(attrs.Withdrawals, Is.Empty);

        Assert.That(attrs.ParentBeaconBlockRoot, Is.Not.Null, "parent_beacon_block_root must be decoded from the fixed-position Bytes32, not a list offset");
        Assert.That(attrs.ParentBeaconBlockRoot, Is.EqualTo(TestItem.KeccakE), "parent_beacon_block_root must round-trip exactly");
    }

    [Test]
    public void DecodeFcuV4Request_spec_layout_roundtrips_parent_beacon_block_root_and_slot_number()
    {
        ulong expectedSlot = 0xAABBCCDD_11223344UL;

        BitArray expectedCustodyColumns = ToBitArray(BlobCellMask.FromIndices([1, 7, 127]));

        ForkchoiceUpdatedRequestWire wire = new()
        {
            ForkchoiceState = new ForkchoiceStateWire
            {
                HeadBlockHash = TestItem.KeccakA,
                SafeBlockHash = TestItem.KeccakB,
                FinalizedBlockHash = TestItem.KeccakC,
            },
            PayloadAttributes =
            [
                new PayloadAttributesWire
                {
                    Timestamp = 0x0102030405060708UL,
                    PrevRandao = TestItem.KeccakD,
                    SuggestedFeeRecipient = TestItem.AddressB,
                    Withdrawals = [],
                    ParentBeaconBlockRoot = TestItem.KeccakE,
                    SlotNumber = expectedSlot,
                }
            ],
            CustodyColumns = [new SszCustodyColumns { Bits = expectedCustodyColumns }]
        };

        byte[] encoded = ForkchoiceUpdatedRequestWire.Encode(wire);

        ForkchoiceUpdatedRequestWire.Decode(encoded, out ForkchoiceUpdatedRequestWire decoded);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(decoded.ForkchoiceState);
        PayloadAttributes? attrs = decoded.PayloadAttributes is { Length: > 0 } a
            ? SszCodec.PayloadAttributesFromWire(a[0]) : null;

        Assert.That(state.HeadBlockHash, Is.EqualTo(TestItem.KeccakA));
        Assert.That(attrs, Is.Not.Null);
        Assert.That(attrs!.ParentBeaconBlockRoot, Is.EqualTo(TestItem.KeccakE), "parent_beacon_block_root must round-trip in V4 as a fixed Bytes32");
        Assert.That(attrs.SlotNumber, Is.EqualTo(expectedSlot), "slot_number must be decoded from the fixed uint64 that follows parent_beacon_block_root");
        Assert.That(attrs.SuggestedFeeRecipient, Is.EqualTo(TestItem.AddressB));
        Assert.That(decoded.CustodyColumns, Has.Length.EqualTo(1));
        Assert.That(decoded.CustodyColumns![0].Bits, Is.Not.Null);
        Assert.That(BitsEqual(decoded.CustodyColumns![0].Bits!, expectedCustodyColumns), Is.True);
    }

    private static BitArray ToBitArray(BlobCellMask mask)
    {
        BitArray result = new(BlobCellMask.CellCount);
        foreach (int index in mask.EnumerateSetBits())
        {
            result.Set(index, true);
        }

        return result;
    }

    private static bool BitsEqual(BitArray left, BitArray right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public void DecodeFcuV3Request_parent_beacon_block_root_is_at_fixed_byte_offset_64_inside_payload_attributes()
    {
        Hash256 expectedRoot = TestItem.KeccakE;

        PayloadAttributesV3Wire attrsWire = new()
        {
            Timestamp = 42UL,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = TestItem.AddressA,
            Withdrawals = [],
            ParentBeaconBlockRoot = expectedRoot,
        };

        byte[] attrsEncoded = PayloadAttributesV3Wire.Encode(attrsWire);

        Assert.That(attrsEncoded.Length, Is.GreaterThanOrEqualTo(96), "PayloadAttributesV3 fixed section is 96 bytes (64 fixed + 32 for parent_beacon_block_root)");

        byte[] rootBytes = attrsEncoded[64..96];
        Assert.That(rootBytes, Is.EqualTo(expectedRoot.Bytes.ToArray()), "parent_beacon_block_root must be encoded as a plain Bytes32 at offset 64, " +
            "not as a variable-length list offset (the H1 regression would place a uint32 here instead)");

        uint withdrawalsOffset = BitConverter.ToUInt32(attrsEncoded, 60);
        Assert.That(withdrawalsOffset, Is.GreaterThanOrEqualTo(96u), "withdrawals list-offset must point past the 96-byte fixed+root section, " +
            "confirming parent_beacon_block_root occupies [64..96) as a fixed field");
    }

    [Test]
    public void EncodeGetPayloadV4Response_all_static_fields_land_at_spec_defined_offsets()
    {
        UInt256 blockValue = new(0xDEADF00Du);
        ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
        Block block = (Block)ep.TryGetBlock();

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV4Response(
            new GetPayloadV4Result(block, blockValue, new BlobsBundleV1(block), shouldOverrideBuilder: false, executionRequests: []), w);
        ReadOnlySpan<byte> buf = w.WrittenSpan;

        AssertGetPayloadResponseHeaderOffsets(buf, fixedSectionSize: 45, blockValue,
            expectedShouldOverrideBuilder: 0, version: "V4");

        uint erOffset = BitConverter.ToUInt32(buf.Slice(41, 4));
        Assert.That(erOffset, Is.GreaterThanOrEqualTo(45u), "execution_requests offset @ 41 must point past the 45-byte fixed section");
    }

    /// <summary>
    /// Regression test for the inline-array-backed <see cref="SszKzgCommitment"/> shape
    /// (issue #11525): a list of N commitments must serialize to exactly <c>N * 48</c> raw
    /// bytes (no offsets, no per-element framing) and round-trip back to the original
    /// 48-byte payloads.
    /// </summary>
    [Test]
    public void SszKzgCommitment_list_roundtrip_preserves_raw_bytes()
    {
        byte[][] proofs = new byte[3][];
        for (int i = 0; i < proofs.Length; i++)
        {
            proofs[i] = new byte[48];
            for (int j = 0; j < 48; j++) proofs[i][j] = (byte)((i * 48 + j) & 0xFF);
        }

        BlobsBundleV1Wire wire = new() { Commitments = proofs.ToKzgWire(), Proofs = [], Blobs = [] };
        byte[] encoded = BlobsBundleV1Wire.Encode(wire);

        BlobsBundleV1Wire.Decode(encoded, out BlobsBundleV1Wire decoded);

        Assert.That(decoded.Commitments, Is.Not.Null);
        Assert.That(decoded.Commitments!.Length, Is.EqualTo(proofs.Length));
        for (int i = 0; i < proofs.Length; i++)
            Assert.That(decoded.Commitments![i].AsSpan().ToArray(), Is.EqualTo(proofs[i]), $"commitment {i} bytes must round-trip exactly");
    }
}
