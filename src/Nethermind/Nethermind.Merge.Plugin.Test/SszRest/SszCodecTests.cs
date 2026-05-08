// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
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
        w.WrittenCount.Should().Be(n);
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
        encoded[0].Should().Be(expected);
    }

    [Test]
    public void EncodePayloadStatus_with_error_is_larger_than_without()
    {
        byte[] withError = Encode(new PayloadStatusV1 { Status = PayloadStatus.Invalid, ValidationError = "bad" }, SszCodec.EncodePayloadStatus);
        byte[] withoutError = Encode(new PayloadStatusV1 { Status = PayloadStatus.Invalid }, SszCodec.EncodePayloadStatus);
        withError.Length.Should().BeGreaterThan(withoutError.Length);
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

        withPrefix.Should().BeEquivalentTo(withoutPrefix);
    }

    [Test]
    public void TransitionConfiguration_roundtrip()
    {
        TransitionConfigurationV1 original = new()
        {
            TerminalTotalDifficulty = new UInt256(100_000_000),
            TerminalBlockHash = TestItem.KeccakA,
            TerminalBlockNumber = 15_000_000
        };

        byte[] encoded = Encode(original, SszCodec.EncodeTransitionConfigurationResponse);
        TransitionConfigurationV1 decoded = SszCodec.DecodeTransitionConfigurationRequest(Seq(encoded));

        decoded.TerminalTotalDifficulty.Should().Be(original.TerminalTotalDifficulty);
        decoded.TerminalBlockHash.Should().Be(original.TerminalBlockHash);
        decoded.TerminalBlockNumber.Should().Be(original.TerminalBlockNumber);
    }

    [Test]
    public void TransitionConfiguration_max_uint256_roundtrip()
    {
        UInt256 max = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");
        byte[] encoded = Encode(new TransitionConfigurationV1
        {
            TerminalTotalDifficulty = max,
            TerminalBlockHash = TestItem.KeccakA,
            TerminalBlockNumber = 1
        }, SszCodec.EncodeTransitionConfigurationResponse);
        TransitionConfigurationV1 decoded = SszCodec.DecodeTransitionConfigurationRequest(Seq(encoded));

        decoded.TerminalTotalDifficulty.Should().Be(max);
    }

    [Test]
    public void Capabilities_roundtrip()
    {
        string[] caps = ["POST /engine/v5/payloads", "GET /engine/v6/payloads/{id}", "POST /engine/v4/forkchoice"];

        byte[] encoded = Encode<IReadOnlyList<string>>(caps, SszCodec.EncodeCapabilitiesResponse);
        string[] decoded = SszCodec.DecodeCapabilitiesRequest(Seq(encoded));

        decoded.Should().BeEquivalentTo(caps);
    }

    [Test]
    public void DecodeGetBlobsRequest_roundtrip()
    {
        byte[] hash = TestItem.KeccakA.Bytes.ToArray();
        byte[] request = new byte[4 + 32];
        request[0] = 0x04;
        Buffer.BlockCopy(hash, 0, request, 4, 32);

        byte[][] decoded = SszCodec.DecodeGetBlobsRequest(Seq(request));

        decoded.Should().HaveCount(1);
        decoded[0].Should().BeEquivalentTo(hash);
    }

    [Test]
    public void DecodeGetPayloadBodiesByHashRequest_roundtrip()
    {
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB];
        byte[] request = new byte[4 + hashes.Length * 32];
        request[0] = 0x04;
        for (int i = 0; i < hashes.Length; i++)
            Buffer.BlockCopy(hashes[i].Bytes.ToArray(), 0, request, 4 + i * 32, 32);

        SszCodec.DecodeGetPayloadBodiesByHashRequest(Seq(request)).Should().BeEquivalentTo(hashes);
    }

    [Test]
    public void DecodeGetPayloadBodiesByRangeRequest_roundtrip()
    {
        byte[] request = new byte[16];
        BitConverter.TryWriteBytes(request.AsSpan(0, 8), 10UL);
        BitConverter.TryWriteBytes(request.AsSpan(8, 8), 5UL);

        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(Seq(request));

        start.Should().Be(10);
        count.Should().Be(5);
    }

    [Test]
    public void EncodePayloadBodiesV1Response_null_body_encodes_as_empty_inner_list()
    {
        byte[] withNull = Encode<IReadOnlyList<ExecutionPayloadBodyV1Result?>>([null], SszCodec.EncodePayloadBodiesV1Response);
        byte[] withPresent = Encode<IReadOnlyList<ExecutionPayloadBodyV1Result?>>([new ExecutionPayloadBodyV1Result([], null)], SszCodec.EncodePayloadBodiesV1Response);

        withPresent.Length.Should().BeGreaterThanOrEqualTo(withNull.Length);
    }

    [Test]
    public void EncodeGetBlobsV3Response_null_vs_present_entry_differ_in_size()
    {
        byte[] blob = new byte[131072];
        byte[] withNull = Encode<IReadOnlyList<BlobAndProofV2?>>([null], SszCodec.EncodeGetBlobsV3Response);
        byte[] withPresent = Encode<IReadOnlyList<BlobAndProofV2?>>([new BlobAndProofV2(blob, [new byte[48]])], SszCodec.EncodeGetBlobsV3Response);

        withPresent.Length.Should().BeGreaterThan(withNull.Length);
    }

    private static IEnumerable<TestCaseData> NonEmptyEncodings()
    {
        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
            SszCodec.EncodeGetPayloadV1Response(SszTestData.MakeMinimalPayload(), w)))
            .SetName(nameof(Encoded_buffer_is_non_empty) + "_GetPayloadV1");

        yield return new TestCaseData((Action<IBufferWriter<byte>>)(w =>
        {
            ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
            Block block = ep.TryGetBlock().Block!;
            SszCodec.EncodeGetPayloadV3Response(new GetPayloadV3Result(block, UInt256.One, new BlobsBundleV1(block), false), w);
        })).SetName(nameof(Encoded_buffer_is_non_empty) + "_GetPayloadV3");
    }

    [TestCaseSource(nameof(NonEmptyEncodings))]
    public void Encoded_buffer_is_non_empty(Action<IBufferWriter<byte>> encode)
    {
        ArrayBufferWriter<byte> w = new();
        encode(w);
        w.WrittenSpan.ToArray().Should().NotBeEmpty();
    }

    private static void AssertCommonNewPayloadFields(
        byte[]?[] hashes, Hash256[] expectedHashes,
        Hash256? parentBeaconBlockRoot, Hash256 expectedParentRoot,
        byte[][]? requests, byte[] expectedRequest)
    {
        hashes.Should().HaveCount(expectedHashes.Length);
        for (int i = 0; i < expectedHashes.Length; i++)
            hashes[i].Should().BeEquivalentTo(expectedHashes[i].Bytes.ToArray());

        parentBeaconBlockRoot.Should().NotBeNull();
        parentBeaconBlockRoot.Should().Be(expectedParentRoot);

        requests.Should().NotBeNull().And.HaveCount(1);
        requests![0].Should().BeEquivalentTo(expectedRequest);
    }

    [Test]
    public void DecodeNewPayload_v4_roundtrip_preserves_all_fields()
    {
        byte[] executionRequest = [0x01, 0x02, 0x03, 0x04];

        NewPayloadV4RequestWire wire = new()
        {
            ExecutionPayload = new SszExecutionPayloadV3(SszTestData.MakeV3Payload()),
            ExpectedBlobVersionedHashes = [TestItem.KeccakA, TestItem.KeccakB],
            ParentBeaconBlockRoot = TestItem.KeccakC,
            ExecutionRequests = [new SszTransaction { Bytes = executionRequest }]
        };

        byte[] encoded = NewPayloadV4RequestWire.Encode(wire);

        NewPayloadV4RequestWire.Decode(encoded, out NewPayloadV4RequestWire decoded);
        ExecutionPayloadV3 payload = decoded.ExecutionPayload.Unwrap();
        byte[]?[] hashes = decoded.ExpectedBlobVersionedHashes.ToBytesArrays();
        byte[][]? requests = decoded.ExecutionRequests.ToExecutionRequests();

        payload.BlockNumber.Should().Be(100);
        payload.GasLimit.Should().Be(2_000_000);
        payload.Timestamp.Should().Be(1_700_000_100);
        payload.BlockHash.Should().Be(TestItem.KeccakE);
        payload.BlobGasUsed.Should().Be(0x20000UL);
        payload.ExcessBlobGas.Should().Be(0x40000UL);

        AssertCommonNewPayloadFields(
            hashes, [TestItem.KeccakA, TestItem.KeccakB],
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
            ExpectedBlobVersionedHashes = [TestItem.KeccakA],
            ParentBeaconBlockRoot = TestItem.KeccakD,
            ExecutionRequests = [new SszTransaction { Bytes = executionRequest }]
        };

        byte[] encoded = NewPayloadV5RequestWire.Encode(wire);

        NewPayloadV5RequestWire.Decode(encoded, out NewPayloadV5RequestWire decoded);
        ExecutionPayloadV4 payload = decoded.ExecutionPayload.Unwrap();
        byte[]?[] hashes = decoded.ExpectedBlobVersionedHashes.ToBytesArrays();
        byte[][]? requests = decoded.ExecutionRequests.ToExecutionRequests();

        payload.BlockNumber.Should().Be(100);
        payload.Timestamp.Should().Be(1_700_000_100);
        payload.BlockHash.Should().Be(TestItem.KeccakE);

        Span<byte> blockAccessListSpan = payload.BlockAccessList;
        blockAccessListSpan.ToArray().Should().BeEquivalentTo(blockAccessList);
        payload.SlotNumber.Should().Be(slotNumber);
        payload.BlobGasUsed.Should().Be(0x20000UL);
        payload.ExcessBlobGas.Should().Be(0x40000UL);

        AssertCommonNewPayloadFields(
            hashes, [TestItem.KeccakA],
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
        w.WrittenCount.Should().BePositive();
    }

    [Test]
    public void EncodeGetPayloadV1Response_fields_land_at_spec_defined_offsets()
    {
        ExecutionPayload ep = SszTestData.MakeMinimalPayload();
        ep.BaseFeePerGas = new UInt256(0xABCDEF);

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV1Response(ep, w);
        ReadOnlySpan<byte> buffer = w.WrittenSpan;

        buffer.Length.Should().BeGreaterThan(440 + 32, "encoded payload must be large enough to contain baseFeePerGas");

        UInt256 decodedBaseFee = new(buffer.Slice(440, 32), isBigEndian: false);
        decodedBaseFee.Should().Be(ep.BaseFeePerGas,
            "baseFeePerGas must be encoded at byte offset 440 per the Ethereum consensus spec");

        buffer.Slice(0, 32).ToArray().Should().BeEquivalentTo(ep.ParentHash!.Bytes.ToArray(),
            "parent_hash must be the first 32 bytes of the encoded payload");

        buffer.Slice(472, 32).ToArray().Should().BeEquivalentTo(ep.BlockHash!.Bytes.ToArray(),
            "block_hash must be encoded at byte offset 472 per the Ethereum consensus spec");
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

        buf.Slice(0, 32).ToArray().Should()
            .BeEquivalentTo(ep.ParentHash!.Bytes.ToArray(), "parent_hash @ offset 0");

        buf.Slice(32, 20).ToArray().Should()
            .BeEquivalentTo(ep.FeeRecipient!.Bytes.ToArray(), "fee_recipient @ offset 32");

        buf.Slice(52, 32).ToArray().Should()
            .BeEquivalentTo(ep.StateRoot!.Bytes.ToArray(), "state_root @ offset 52");

        buf.Slice(84, 32).ToArray().Should()
            .BeEquivalentTo(ep.ReceiptsRoot!.Bytes.ToArray(), "receipts_root @ offset 84");

        buf.Slice(116, 256).ToArray().Should()
            .BeEquivalentTo(Bloom.Empty.Bytes.ToArray(), "logs_bloom @ offset 116");

        buf.Slice(372, 32).ToArray().Should()
            .BeEquivalentTo(ep.PrevRandao!.Bytes.ToArray(), "prev_randao @ offset 372");

        BitConverter.ToUInt64(buf.Slice(404, 8)).Should()
            .Be((ulong)ep.BlockNumber, "block_number @ offset 404");

        BitConverter.ToUInt64(buf.Slice(412, 8)).Should()
            .Be((ulong)ep.GasLimit, "gas_limit @ offset 412");

        BitConverter.ToUInt64(buf.Slice(420, 8)).Should()
            .Be((ulong)ep.GasUsed, "gas_used @ offset 420");

        BitConverter.ToUInt64(buf.Slice(428, 8)).Should()
            .Be(ep.Timestamp, "timestamp @ offset 428");

        uint extraDataOffset = BitConverter.ToUInt32(buf.Slice(436, 4));
        extraDataOffset.Should().BeGreaterThanOrEqualTo(508u,
            "extra_data variable-length offset @ offset 436 must point past the fixed section");

        new UInt256(buf.Slice(440, 32), isBigEndian: false).Should()
            .Be(ep.BaseFeePerGas, "base_fee_per_gas @ offset 440");

        buf.Slice(472, 32).ToArray().Should()
            .BeEquivalentTo(ep.BlockHash!.Bytes.ToArray(), "block_hash @ offset 472");

        uint txOffset = BitConverter.ToUInt32(buf.Slice(504, 4));
        txOffset.Should().BeGreaterThanOrEqualTo(508u,
            "transactions variable-length offset @ offset 504 must point past the fixed section");
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

        buf[0].Should().Be(0, "status=VALID must encode as 0x00 at offset 0");

        uint lvhOffset = BitConverter.ToUInt32(buf.Slice(1, 4));
        lvhOffset.Should().BeGreaterThanOrEqualTo(9u,
            "latest_valid_hash variable-length offset @ 1 must point past the 9-byte fixed section");

        uint veOffset = BitConverter.ToUInt32(buf.Slice(5, 4));
        veOffset.Should().BeGreaterThanOrEqualTo(9u,
            "validation_error variable-length offset @ 5 must point past the 9-byte fixed section");

        buf.Slice((int)lvhOffset, 32).ToArray().Should()
            .BeEquivalentTo(TestItem.KeccakA.Bytes.ToArray(),
                "latest_valid_hash bytes must land at the offset encoded in the fixed section");
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

        buf.Length.Should().BeGreaterThanOrEqualTo(8,
            "encoded response must cover the 8-byte fixed section (two 4-byte offsets)");

        uint psOffset = BitConverter.ToUInt32(buf.Slice(0, 4));
        psOffset.Should().BeGreaterThanOrEqualTo(8u,
            "payload_status offset @ 0 must point past the 8-byte fixed section");
        psOffset.Should().BeLessThan((uint)buf.Length,
            "payload_status offset must be within the encoded buffer");

        uint pidOffset = BitConverter.ToUInt32(buf.Slice(4, 4));
        pidOffset.Should().BeGreaterThanOrEqualTo(8u,
            "payload_id offset @ 4 must point past the 8-byte fixed section");
        pidOffset.Should().BeLessThan((uint)buf.Length,
            "payload_id offset must be within the encoded buffer");

        buf[(int)psOffset].Should().Be(0,
            "first byte of the embedded PayloadStatus sub-container must be 0x00 (VALID)");

        int pidEnd = (int)pidOffset + 8;
        buf.Length.Should().BeGreaterThanOrEqualTo(pidEnd,
            "encoded buffer must be large enough to hold the payload_id bytes");

        buf.Slice((int)pidOffset, 8).ToArray().Should()
            .BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
                "payload_id bytes must match the original hex string");
    }

    private static void AssertGetPayloadResponseHeaderOffsets(
        ReadOnlySpan<byte> buf,
        int fixedSectionSize,
        UInt256 expectedBlockValue,
        byte expectedShouldOverrideBuilder,
        string version)
    {
        buf.Length.Should().BeGreaterThanOrEqualTo(fixedSectionSize,
            $"encoded {version} response must cover the {fixedSectionSize}-byte fixed section");

        uint epOffset = BitConverter.ToUInt32(buf.Slice(0, 4));
        epOffset.Should().BeGreaterThanOrEqualTo((uint)fixedSectionSize,
            $"execution_payload offset @ 0 must point past the {fixedSectionSize}-byte fixed section");

        new UInt256(buf.Slice(4, 32), isBigEndian: false).Should()
            .Be(expectedBlockValue, "block_value must be encoded at byte offset 4");

        uint bbOffset = BitConverter.ToUInt32(buf.Slice(36, 4));
        bbOffset.Should().BeGreaterThanOrEqualTo((uint)fixedSectionSize,
            $"blobs_bundle offset @ 36 must point past the {fixedSectionSize}-byte fixed section");

        buf[40].Should().Be(expectedShouldOverrideBuilder,
            $"should_override_builder must encode as 0x{expectedShouldOverrideBuilder:X2} at offset 40");
    }

    [Test]
    public void EncodeGetPayloadV3Response_all_static_fields_land_at_spec_defined_offsets()
    {
        UInt256 blockValue = new(0xCAFEBABEu);
        ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
        Block block = ep.TryGetBlock().Block!;

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

        state.HeadBlockHash.Should().Be(TestItem.KeccakA);
        state.SafeBlockHash.Should().Be(TestItem.KeccakB);
        state.FinalizedBlockHash.Should().Be(TestItem.KeccakC);

        attrs.Should().NotBeNull();
        attrs!.Timestamp.Should().Be(0x1122334455667788UL);
        attrs.PrevRandao.Should().Be(TestItem.KeccakD);
        attrs.SuggestedFeeRecipient.Should().Be(TestItem.AddressA);
        attrs.Withdrawals.Should().BeEmpty();

        attrs.ParentBeaconBlockRoot.Should().NotBeNull(
            "parent_beacon_block_root must be decoded from the fixed-position Bytes32, not a list offset");
        attrs.ParentBeaconBlockRoot.Should().Be(TestItem.KeccakE,
            "parent_beacon_block_root must round-trip exactly");
    }

    [Test]
    public void DecodeFcuV4Request_spec_layout_roundtrips_parent_beacon_block_root_and_slot_number()
    {
        ulong expectedSlot = 0xAABBCCDD_11223344UL;

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
            ]
        };

        byte[] encoded = ForkchoiceUpdatedRequestWire.Encode(wire);

        ForkchoiceUpdatedRequestWire.Decode(encoded, out ForkchoiceUpdatedRequestWire decoded);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(decoded.ForkchoiceState);
        PayloadAttributes? attrs = decoded.PayloadAttributes is { Length: > 0 } a
            ? SszCodec.PayloadAttributesFromWire(a[0]) : null;

        state.HeadBlockHash.Should().Be(TestItem.KeccakA);
        attrs.Should().NotBeNull();
        attrs!.ParentBeaconBlockRoot.Should().Be(TestItem.KeccakE,
            "parent_beacon_block_root must round-trip in V4 as a fixed Bytes32");
        attrs.SlotNumber.Should().Be(expectedSlot,
            "slot_number must be decoded from the fixed uint64 that follows parent_beacon_block_root");
        attrs.SuggestedFeeRecipient.Should().Be(TestItem.AddressB);
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

        attrsEncoded.Length.Should().BeGreaterThanOrEqualTo(96,
            "PayloadAttributesV3 fixed section is 96 bytes (64 fixed + 32 for parent_beacon_block_root)");

        byte[] rootBytes = attrsEncoded[64..96];
        rootBytes.Should().BeEquivalentTo(expectedRoot.Bytes.ToArray(),
            "parent_beacon_block_root must be encoded as a plain Bytes32 at offset 64, " +
            "not as a variable-length list offset (the H1 regression would place a uint32 here instead)");

        uint withdrawalsOffset = BitConverter.ToUInt32(attrsEncoded, 60);
        withdrawalsOffset.Should().BeGreaterThanOrEqualTo(96u,
            "withdrawals list-offset must point past the 96-byte fixed+root section, " +
            "confirming parent_beacon_block_root occupies [64..96) as a fixed field");
    }

    [Test]
    public void EncodeGetPayloadV4Response_all_static_fields_land_at_spec_defined_offsets()
    {
        UInt256 blockValue = new(0xDEADF00Du);
        ExecutionPayloadV3 ep = SszTestData.MakeV3Payload();
        Block block = ep.TryGetBlock().Block!;

        ArrayBufferWriter<byte> w = new();
        SszCodec.EncodeGetPayloadV4Response(
            new GetPayloadV4Result(block, blockValue, new BlobsBundleV1(block), shouldOverrideBuilder: false, executionRequests: []), w);
        ReadOnlySpan<byte> buf = w.WrittenSpan;

        AssertGetPayloadResponseHeaderOffsets(buf, fixedSectionSize: 45, blockValue,
            expectedShouldOverrideBuilder: 0, version: "V4");

        uint erOffset = BitConverter.ToUInt32(buf.Slice(41, 4));
        erOffset.Should().BeGreaterThanOrEqualTo(45u,
            "execution_requests offset @ 41 must point past the 45-byte fixed section");
    }
}
