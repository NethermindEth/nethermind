// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class NativeStreamTests
{
    private const ulong RollupId = 0x0102030405060708;
    private static readonly Address From = new("0x1111111111111111111111111111111111111111");
    private static readonly Address To = new("0x2222222222222222222222222222222222222222");

    [Test]
    public void Decode_OperationsAndClose_ReadsLittleEndianFields()
    {
        byte[] stream = Bytes.FromHexString("0002080706050403020101aa01");

        NativeStream decoded = NativeStream.Decode(stream, RollupId);

        Assert.That(decoded.Operations.ToArray(), Is.EqualTo(new byte[] { 0xaa }));
        Assert.That(decoded.Messages, Is.Empty);
        Assert.That(NativeStream.Encode(RollupId, decoded.Operations.Span, decoded.Messages), Is.EqualTo(stream), "the encoder writes the same bytes");
    }

    [Test]
    public void Decode_PaddedLengthVarint_IsAccepted()
    {
        byte[] stream = Bytes.FromHexString("000207000000000000008100aa01");

        Assert.That(NativeStream.Decode(stream, 7).Operations.ToArray(), Is.EqualTo(new byte[] { 0xaa }),
            "stream lengths may carry padding groups, unlike the operations' own varints");
    }

    [TestCase("00020700000000000000ffffffff1001", TestName = "LengthVarintOverflow")]
    [TestCase("010207000000000000000001", TestName = "UnknownVersion")]
    [TestCase("0002070000000000000000", TestName = "MissingClose")]
    [TestCase("000207000000000000000001000100", TestName = "NonZeroPadding")]
    [TestCase("00020700000000000000000b01", TestName = "UnknownMessageType")]
    [TestCase("000207000000000000000002070000000000000000" + "01", TestName = "SecondChainOperation")]
    public void Decode_MalformedStream_Throws(string hex) =>
        Assert.Throws<EezSettlementException>(() => NativeStream.Decode(Bytes.FromHexString(hex), 7));

    [Test]
    public void Decode_AnotherRollupsStream_Throws()
    {
        byte[] stream = NativeStream.Encode(8, [], []);

        Assert.Throws<EezSettlementException>(() => NativeStream.Decode(stream, 7));
    }

    [Test]
    public void Encode_NestedTransactionWithSnapshot_RoundTripsEveryMessage()
    {
        StreamMessage[] messages =
        [
            new StreamMessage.Initiate(RollupId, new byte[] { 0x01, 0x02 }),
            new StreamMessage.Call(0, From, To, 5, 0, new byte[] { 0xab }),
            new StreamMessage.Snapshot(),
            new StreamMessage.StaticCall(RollupId, To, From, 0, new byte[] { 0xcd }),
            new StreamMessage.Return(false, new byte[] { 0xee }),
            new StreamMessage.Revert(),
            new StreamMessage.Return(true, ReadOnlyMemory<byte>.Empty),
            new StreamMessage.Finish(),
        ];

        byte[] stream = NativeStream.Encode(RollupId, new byte[] { 0xaa }, messages);
        NativeStream decoded = NativeStream.Decode(Bytes.Concat(stream, new byte[3]), RollupId);

        Assert.That(decoded.Messages.Length, Is.EqualTo(messages.Length));
        StreamMessage.Call call = (StreamMessage.Call)decoded.Messages[1];
        Assert.That(call.Value, Is.EqualTo((UInt256)5));
        Assert.That(call.To, Is.EqualTo(To));
        Assert.That(((StreamMessage.Return)decoded.Messages[4]).Success, Is.False);
        Assert.That(NativeStream.Encode(RollupId, decoded.Operations.Span, decoded.Messages), Is.EqualTo(stream), "zero padding is dropped");
    }

    [TestCaseSource(nameof(UnbalancedBrackets))]
    public void Encode_UnbalancedBrackets_Throws(StreamMessage[] messages) =>
        Assert.Throws<EezSettlementException>(() => NativeStream.Encode(RollupId, [], messages));

    [Test]
    public void Encode_CallsNestedDeeperThanTheLimit_Throws()
    {
        StreamMessage[] messages =
        [
            new StreamMessage.Initiate(RollupId, ReadOnlyMemory<byte>.Empty),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Call(0, From, To, 0, 0, ReadOnlyMemory<byte>.Empty), NativeStream.MaxCallDepth),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Return(true, ReadOnlyMemory<byte>.Empty), NativeStream.MaxCallDepth),
            new StreamMessage.Finish(),
        ];

        Assert.That(() => NativeStream.Encode(RollupId, [], messages), Throws.TypeOf<EezSettlementException>().With.Message.Contains("calls nest too deep"));
    }

    [Test]
    public void Encode_CallsAtTheDepthLimit_RoundTrips()
    {
        int calls = NativeStream.MaxCallDepth - 1;
        StreamMessage[] messages =
        [
            new StreamMessage.Initiate(RollupId, ReadOnlyMemory<byte>.Empty),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Call(0, From, To, 0, 0, ReadOnlyMemory<byte>.Empty), calls),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Return(true, ReadOnlyMemory<byte>.Empty), calls),
            new StreamMessage.Finish(),
        ];
        byte[] encoded = NativeStream.Encode(RollupId, [], messages);
        Assert.That(NativeStream.Decode(encoded, RollupId).Messages.Length, Is.EqualTo(messages.Length));
    }

    [TestCase(NativeStream.MaxCallDepth, true)]
    [TestCase(NativeStream.MaxCallDepth + 1, false)]
    public void Encode_SnapshotDepth_EnforcesTheBoundary(int depth, bool accepted)
    {
        StreamMessage[] messages =
        [
            new StreamMessage.Initiate(RollupId, ReadOnlyMemory<byte>.Empty),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Snapshot(), depth),
            .. Enumerable.Repeat<StreamMessage>(new StreamMessage.Revert(), depth),
            new StreamMessage.Finish(),
        ];
        if (accepted)
        {
            byte[] encoded = NativeStream.Encode(RollupId, [], messages);
            Assert.That(NativeStream.Decode(encoded, RollupId).Messages.Length, Is.EqualTo(messages.Length));
        }
        else
        {
            Assert.That(() => NativeStream.Encode(RollupId, [], messages), Throws.TypeOf<EezSettlementException>().With.Message.Contains("snapshots nest too deep"));
        }
    }

    private static TestCaseData[] UnbalancedBrackets()
    {
        StreamMessage.Initiate initiate = new(RollupId, ReadOnlyMemory<byte>.Empty);
        StreamMessage.Call call = new(0, From, To, 0, 0, ReadOnlyMemory<byte>.Empty);
        StreamMessage.Return success = new(true, ReadOnlyMemory<byte>.Empty);
        return
        [
            new TestCaseData((object)new StreamMessage[] { call }).SetName("CallOutsideTransaction"),
            new TestCaseData((object)new StreamMessage[] { initiate, initiate }).SetName("NestedTransaction"),
            new TestCaseData((object)new StreamMessage[] { initiate, success }).SetName("ReturnWithoutCall"),
            new TestCaseData((object)new StreamMessage[] { initiate, call, new StreamMessage.Finish() }).SetName("FinishWithOpenCall"),
            new TestCaseData((object)new StreamMessage[] { initiate, new StreamMessage.Snapshot(), new StreamMessage.Finish() }).SetName("FinishWithOpenSnapshot"),
            new TestCaseData((object)new StreamMessage[] { initiate, call, new StreamMessage.Snapshot(), success }).SetName("ReturnCrossesSnapshot"),
            new TestCaseData((object)new StreamMessage[] { initiate, new StreamMessage.Revert() }).SetName("RevertWithoutSnapshot"),
            new TestCaseData((object)new StreamMessage[] { initiate, new StreamMessage.Snapshot(), call, new StreamMessage.Revert() }).SetName("RevertWithOpenCall"),
            new TestCaseData((object)new StreamMessage[] { initiate, call }).SetName("UnclosedTransaction"),
        ];
    }
}
