// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.SszRest;

[Parallelizable(ParallelScope.Self)]
public class SszCodecTests
{
    private static byte[] ToBytes((byte[] buffer, int length) pooled)
    {
        try
        {
            return pooled.buffer.AsSpan(0, pooled.length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled.buffer);
        }
    }

    [TestCase(PayloadStatus.Valid, (byte)0)]
    [TestCase(PayloadStatus.Invalid, (byte)1)]
    [TestCase(PayloadStatus.Syncing, (byte)2)]
    [TestCase(PayloadStatus.Accepted, (byte)3)]
    public void EncodePayloadStatus_status_byte_is_correct(string status, byte expected)
    {
        byte[] encoded = ToBytes(SszCodec.EncodePayloadStatus(new PayloadStatusV1 { Status = status }));
        encoded[0].Should().Be(expected);
    }

    [Test]
    public void EncodePayloadStatus_with_error_is_larger_than_without()
    {
        byte[] withError = ToBytes(SszCodec.EncodePayloadStatus(new PayloadStatusV1 { Status = PayloadStatus.Invalid, ValidationError = "bad" }));
        byte[] withoutError = ToBytes(SszCodec.EncodePayloadStatus(new PayloadStatusV1 { Status = PayloadStatus.Invalid }));
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

        byte[] withPrefix = ToBytes(SszCodec.EncodeForkchoiceUpdatedResponse(WithId("0x0102030405060708")));
        byte[] withoutPrefix = ToBytes(SszCodec.EncodeForkchoiceUpdatedResponse(WithId("0102030405060708")));

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

        byte[] encoded = ToBytes(SszCodec.EncodeTransitionConfigurationResponse(original));
        TransitionConfigurationV1 decoded = SszCodec.DecodeTransitionConfigurationRequest(encoded);

        decoded.TerminalTotalDifficulty.Should().Be(original.TerminalTotalDifficulty);
        decoded.TerminalBlockHash.Should().Be(original.TerminalBlockHash);
        decoded.TerminalBlockNumber.Should().Be(original.TerminalBlockNumber);
    }

    [Test]
    public void TransitionConfiguration_max_uint256_roundtrip()
    {
        UInt256 max = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935");
        byte[] encoded = ToBytes(SszCodec.EncodeTransitionConfigurationResponse(new()
        {
            TerminalTotalDifficulty = max,
            TerminalBlockHash = TestItem.KeccakA,
            TerminalBlockNumber = 1
        }));
        TransitionConfigurationV1 decoded = SszCodec.DecodeTransitionConfigurationRequest(encoded);

        decoded.TerminalTotalDifficulty.Should().Be(max);
    }

    [Test]
    public void Capabilities_roundtrip()
    {
        string[] caps = ["POST /engine/v5/payloads", "GET /engine/v6/payloads/{id}", "POST /engine/v4/forkchoice"];

        // Request and response containers share the same SSZ shape.
        byte[] encoded = ToBytes(SszCodec.EncodeCapabilitiesResponse(caps));
        string[] decoded = SszCodec.DecodeCapabilitiesRequest(encoded);

        decoded.Should().BeEquivalentTo(caps);
    }

    [Test]
    public void DecodeGetBlobsRequest_roundtrip()
    {
        byte[] hash = TestItem.KeccakA.Bytes.ToArray();
        byte[] request = new byte[4 + 32];
        request[0] = 0x04;
        Buffer.BlockCopy(hash, 0, request, 4, 32);

        byte[][] decoded = SszCodec.DecodeGetBlobsRequest(request);

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

        SszCodec.DecodeGetPayloadBodiesByHashRequest(request).Should().BeEquivalentTo(hashes);
    }

    [Test]
    public void DecodeGetPayloadBodiesByRangeRequest_roundtrip()
    {
        byte[] request = new byte[16];
        BitConverter.TryWriteBytes(request.AsSpan(0, 8), 10UL);
        BitConverter.TryWriteBytes(request.AsSpan(8, 8), 5UL);

        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(request);

        start.Should().Be(10);
        count.Should().Be(5);
    }

    [Test]
    public void EncodePayloadBodiesV1Response_null_body_encodes_as_empty_inner_list()
    {
        byte[] withNull = ToBytes(SszCodec.EncodePayloadBodiesV1Response([null]));
        byte[] withPresent = ToBytes(SszCodec.EncodePayloadBodiesV1Response([new ExecutionPayloadBodyV1Result([], null)]));

        withPresent.Length.Should().BeGreaterThanOrEqualTo(withNull.Length);
    }

    [Test]
    public void EncodeGetBlobsV3Response_null_vs_present_entry_differ_in_size()
    {
        byte[] blob = new byte[131072];
        byte[] withNull = ToBytes(SszCodec.EncodeGetBlobsV3Response([null]));
        byte[] withPresent = ToBytes(SszCodec.EncodeGetBlobsV3Response([new BlobAndProofV2(blob, [new byte[48]])]));

        withPresent.Length.Should().BeGreaterThan(withNull.Length);
    }

    [Test]
    public void EncodeGetPayloadV1Response_produces_non_empty_bytes() =>
        ToBytes(SszCodec.EncodeGetPayloadV1Response(MakeMinimalPayload())).Should().NotBeEmpty();

    [Test]
    public void EncodeGetPayloadV3Response_produces_non_empty_bytes()
    {
        ExecutionPayloadV3 ep = MakeV3Payload();
        Block block = ep.TryGetBlock().Block!;
        ToBytes(SszCodec.EncodeGetPayloadV3Response(new GetPayloadV3Result(block, UInt256.One, new BlobsBundleV1(block), false)))
            .Should().NotBeEmpty();
    }

    [Test]
    public void DecodeNewPayload_v4_roundtrip_preserves_all_fields()
    {
        byte[] executionRequest = [0x01, 0x02, 0x03, 0x04];

        NewPayloadV4RequestWire wire = new()
        {
            ExecutionPayload = ExecutionPayloadV3Ssz.Wrap(MakeV3Payload()),
            ExpectedBlobVersionedHashes = [TestItem.KeccakA, TestItem.KeccakB],
            ParentBeaconBlockRoot = TestItem.KeccakC,
            ExecutionRequests = [new SszTransaction { Data = executionRequest }]
        };

        byte[] encoded = NewPayloadV4RequestWire.Encode(wire);

        (ExecutionPayload payload, byte[]?[] hashes, Hash256? parentBeaconBlockRoot, byte[][]? requests) =
            SszCodec.DecodeNewPayloadRequest(encoded, version: 4);

        payload.BlockNumber.Should().Be(100);
        payload.GasLimit.Should().Be(2_000_000);
        payload.Timestamp.Should().Be(1_700_000_100);
        payload.BlockHash.Should().Be(TestItem.KeccakE);
        ((ExecutionPayloadV3)payload).BlobGasUsed.Should().Be(0x20000UL);
        ((ExecutionPayloadV3)payload).ExcessBlobGas.Should().Be(0x40000UL);

        hashes.Should().HaveCount(2);
        hashes[0].Should().BeEquivalentTo(TestItem.KeccakA.Bytes.ToArray());
        hashes[1].Should().BeEquivalentTo(TestItem.KeccakB.Bytes.ToArray());

        parentBeaconBlockRoot.Should().NotBeNull();
        parentBeaconBlockRoot.Should().Be(TestItem.KeccakC);

        requests.Should().NotBeNull();
        requests!.Should().HaveCount(1);
        requests[0].Should().BeEquivalentTo(executionRequest);
    }

    [Test]
    public void DecodeNewPayload_v5_roundtrip_preserves_v4_payload_fields()
    {
        byte[] executionRequest = [0xAA, 0xBB];
        byte[] blockAccessList = [0x01, 0x02, 0x03];
        ulong slotNumber = 999_999UL;

        NewPayloadV5RequestWire wire = new()
        {
            ExecutionPayload = ExecutionPayloadV4Ssz.Wrap(MakeV4Payload(blockAccessList, slotNumber)),
            ExpectedBlobVersionedHashes = [TestItem.KeccakA],
            ParentBeaconBlockRoot = TestItem.KeccakD,
            ExecutionRequests = [new SszTransaction { Data = executionRequest }]
        };

        byte[] encoded = NewPayloadV5RequestWire.Encode(wire);

        (ExecutionPayload payload, byte[]?[] hashes, Hash256? parentBeaconBlockRoot, byte[][]? requests) =
            SszCodec.DecodeNewPayloadRequest(encoded, version: 5);

        payload.BlockNumber.Should().Be(100);
        payload.Timestamp.Should().Be(1_700_000_100);
        payload.BlockHash.Should().Be(TestItem.KeccakE);

        ExecutionPayloadV4 v4 = payload.Should().BeOfType<ExecutionPayloadV4>().Subject;
        Span<byte> blockAccessListSpan = v4.BlockAccessList;
        blockAccessListSpan.ToArray().Should().BeEquivalentTo(blockAccessList);
        v4.SlotNumber.Should().Be(slotNumber);
        v4.BlobGasUsed.Should().Be(0x20000UL);
        v4.ExcessBlobGas.Should().Be(0x40000UL);

        hashes.Should().HaveCount(1);
        hashes[0].Should().BeEquivalentTo(TestItem.KeccakA.Bytes.ToArray());

        parentBeaconBlockRoot.Should().NotBeNull();
        parentBeaconBlockRoot.Should().Be(TestItem.KeccakD);

        requests.Should().NotBeNull().And.HaveCount(1);
        requests![0].Should().BeEquivalentTo(executionRequest);
    }

    [Test]
    public void EncodePayloadStatus_buffer_length_is_consistent()
    {
        (byte[] buffer, int length) = SszCodec.EncodePayloadStatus(new PayloadStatusV1 { Status = PayloadStatus.Valid });
        try
        {
            length.Should().BePositive().And.BeLessThanOrEqualTo(buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Test]
    public void EncodeGetPayloadV1Response_buffer_length_is_consistent()
    {
        (byte[] buffer, int length) = SszCodec.EncodeGetPayloadV1Response(MakeMinimalPayload());
        try
        {
            length.Should().BePositive().And.BeLessThanOrEqualTo(buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Test]
    public void EncodeGetPayloadV1Response_fields_land_at_spec_defined_offsets()
    {
        ExecutionPayload ep = MakeMinimalPayload();
        ep.BaseFeePerGas = new UInt256(0xABCDEF);

        (byte[] buffer, int length) = SszCodec.EncodeGetPayloadV1Response(ep);
        try
        {
            length.Should().BeGreaterThan(440 + 32, "encoded payload must be large enough to contain baseFeePerGas");

            UInt256 decodedBaseFee = new(buffer.AsSpan(440, 32), isBigEndian: false);
            decodedBaseFee.Should().Be(ep.BaseFeePerGas,
                "baseFeePerGas must be encoded at byte offset 440 per the Ethereum consensus spec");

            buffer.AsSpan(0, 32).ToArray().Should().BeEquivalentTo(ep.ParentHash!.Bytes.ToArray(),
                "parent_hash must be the first 32 bytes of the encoded payload");

            buffer.AsSpan(472, 32).ToArray().Should().BeEquivalentTo(ep.BlockHash!.Bytes.ToArray(),
                "block_hash must be encoded at byte offset 472 per the Ethereum consensus spec");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ExecutionPayload MakeMinimalPayload() => new()
    {
        ParentHash = TestItem.KeccakA,
        FeeRecipient = TestItem.AddressA,
        StateRoot = TestItem.KeccakB,
        ReceiptsRoot = TestItem.KeccakC,
        LogsBloom = Bloom.Empty,
        PrevRandao = TestItem.KeccakD,
        BlockNumber = 1,
        GasLimit = 1_000_000,
        GasUsed = 0,
        Timestamp = 1_700_000_000,
        ExtraData = [],
        BaseFeePerGas = 1,
        BlockHash = TestItem.KeccakE,
        Transactions = []
    };

    private static ExecutionPayloadV3 MakeV3Payload() => new()
    {
        ParentHash = TestItem.KeccakA,
        FeeRecipient = TestItem.AddressA,
        StateRoot = TestItem.KeccakB,
        ReceiptsRoot = TestItem.KeccakC,
        LogsBloom = Bloom.Empty,
        PrevRandao = TestItem.KeccakD,
        BlockNumber = 100,
        GasLimit = 2_000_000,
        GasUsed = 50_000,
        Timestamp = 1_700_000_100,
        ExtraData = [],
        BaseFeePerGas = 10,
        BlockHash = TestItem.KeccakE,
        Transactions = [],
        Withdrawals = [],
        BlobGasUsed = 0x20000,
        ExcessBlobGas = 0x40000
    };

    private static ExecutionPayloadV4 MakeV4Payload(byte[] blockAccessList, ulong slotNumber) => new()
    {
        ParentHash = TestItem.KeccakA,
        FeeRecipient = TestItem.AddressA,
        StateRoot = TestItem.KeccakB,
        ReceiptsRoot = TestItem.KeccakC,
        LogsBloom = Bloom.Empty,
        PrevRandao = TestItem.KeccakD,
        BlockNumber = 100,
        GasLimit = 2_000_000,
        GasUsed = 50_000,
        Timestamp = 1_700_000_100,
        ExtraData = [],
        BaseFeePerGas = 10,
        BlockHash = TestItem.KeccakE,
        Transactions = [],
        Withdrawals = [],
        BlobGasUsed = 0x20000,
        ExcessBlobGas = 0x40000,
        BlockAccessList = blockAccessList,
        SlotNumber = slotNumber
    };
}
