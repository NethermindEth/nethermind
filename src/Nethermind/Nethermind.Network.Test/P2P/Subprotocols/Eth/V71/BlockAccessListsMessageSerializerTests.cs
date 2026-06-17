// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V71;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListsMessageSerializerTests
{
    [TestCaseSource(nameof(BlockAccessListsRoundtripCases))]
    public void Roundtrip(Func<BlockAccessListsMessage> buildMessage, string? expectedData)
    {
        BlockAccessListsMessageSerializer serializer = new();
        using BlockAccessListsMessage msg = buildMessage();
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
        using DisposableByteBuffer buffer2 = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();

        serializer.Serialize(buffer, msg);
        using BlockAccessListsMessage deserialized = serializer.Deserialize(buffer);

        AssertBlockAccessListsMessage(deserialized, msg);
        Assert.That(buffer.ReadableBytes, Is.EqualTo(0), "readable bytes");

        serializer.Serialize(buffer2, deserialized);
        buffer.SetReaderIndex(0);
        string allHex = buffer.ReadAllHex();
        Assert.That(buffer2.ReadAllHex(), Is.EqualTo(allHex), "test zero");

        if (expectedData is not null)
        {
            Assert.That(allHex, Is.EqualTo(expectedData));
        }
    }

    [TestCaseSource(nameof(BlockAccessListsRejectionCases))]
    public void Rejects_invalid_payload(Func<IByteBuffer> buildPayload, Type exceptionType)
    {
        BlockAccessListsMessageSerializer serializer = new();
        using DisposableByteBuffer payload = buildPayload().AsDisposable();

        Assert.Throws(exceptionType, () => serializer.Deserialize(payload));
    }

    private static IEnumerable<TestCaseData> BlockAccessListsRoundtripCases()
    {
        yield return new TestCaseData(
                new Func<BlockAccessListsMessage>(() => BuildMessage(42)),
                null)
            .SetName("Roundtrip_empty");
        yield return new TestCaseData(
                new Func<BlockAccessListsMessage>(() => BuildMessage(43, (byte[]?)null)),
                "c32bc180")
            .SetName("Roundtrip_single_absent_bal");
        yield return new TestCaseData(
                new Func<BlockAccessListsMessage>(() => BuildMessage(44, [0xc0])),
                "c32cc1c0")
            .SetName("Roundtrip_single_empty_bal");
        yield return new TestCaseData(
                new Func<BlockAccessListsMessage>(() => BuildMessage(45, [0xc1, 0x80], [0xc2, 0x01, 0x02], null)),
                "c82dc6c180c2010280")
            .SetName("Roundtrip_multiple_bals");
        yield return new TestCaseData(
                new Func<BlockAccessListsMessage>(() => BuildMessage(-1)),
                null)
            .SetName("Roundtrip_negative_request_id");
    }

    private static IEnumerable<TestCaseData> BlockAccessListsRejectionCases()
    {
        yield return new TestCaseData(
                new Func<IByteBuffer>(() => Unpooled.WrappedBuffer([0xc5, 0x01, 0xc2, 0x81, 0x80, 0xc0])),
                typeof(RlpException))
            .SetName("Rejects_extra_outer_payload");
        yield return new TestCaseData(
                new Func<IByteBuffer>(() => Unpooled.WrappedBuffer([0xcb, 0x89, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0xc0])),
                typeof(RlpException))
            .SetName("Rejects_request_id_longer_than_8_bytes");
        yield return new TestCaseData(
                new Func<IByteBuffer>(BuildTooManyBlockAccessListsPayload),
                typeof(RlpLimitException))
            .SetName("Rejects_too_many_block_access_lists");
    }

    private static IByteBuffer BuildTooManyBlockAccessListsPayload()
    {
        BlockAccessListsMessageSerializer serializer = new();
        byte[]?[] blockAccessLists = new byte[]?[GethSyncLimits.MaxBodyFetch + 1];

        using BlockAccessListsMessage msg = BuildMessage(45, blockAccessLists);
        IByteBuffer payload = Unpooled.Buffer(serializer.GetLength(msg, out _));
        serializer.Serialize(payload, msg);
        return payload;
    }

    private static BlockAccessListsMessage BuildMessage(long requestId, params byte[]?[] blockAccessLists) =>
        new(requestId, new ArrayPoolList<byte[]?>(blockAccessLists));

    private static void AssertBlockAccessListsMessage(BlockAccessListsMessage actual, BlockAccessListsMessage expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.BlockAccessLists.Count, Is.EqualTo(expected.BlockAccessLists.Count));
            if (actual.BlockAccessLists.Count != expected.BlockAccessLists.Count)
            {
                return;
            }

            for (int i = 0; i < expected.BlockAccessLists.Count; i++)
            {
                Assert.That(actual.BlockAccessLists[i], Is.EqualTo(expected.BlockAccessLists[i]));
            }
        }
    }
}

[Parallelizable(ParallelScope.All)]
public class GetBlockAccessListsMessageSerializerTests
{
    [TestCaseSource(nameof(GetBlockAccessListsRoundtripCases))]
    public void Roundtrip(Func<GetBlockAccessListsMessage> buildMessage)
    {
        GetBlockAccessListsMessageSerializer serializer = new();
        using GetBlockAccessListsMessage msg = buildMessage();
        SerializerTester.TestZero(serializer, msg);
    }

    private static IEnumerable<TestCaseData> GetBlockAccessListsRoundtripCases()
    {
        yield return new TestCaseData(
                new Func<GetBlockAccessListsMessage>(() => new GetBlockAccessListsMessage(99, ArrayPoolList<Hash256>.Empty())))
            .SetName("Roundtrip_empty_hashes");
        yield return new TestCaseData(
                new Func<GetBlockAccessListsMessage>(() => new GetBlockAccessListsMessage(100, new ArrayPoolList<Hash256>(1)
                {
                    Keccak.Zero
                })))
            .SetName("Roundtrip_single_hash");
        yield return new TestCaseData(
                new Func<GetBlockAccessListsMessage>(() => new GetBlockAccessListsMessage(101, new ArrayPoolList<Hash256>(3)
                {
                    Keccak.Zero,
                    TestItem.KeccakA,
                    TestItem.KeccakB
                })))
            .SetName("Roundtrip_multiple_hashes");
    }
}
