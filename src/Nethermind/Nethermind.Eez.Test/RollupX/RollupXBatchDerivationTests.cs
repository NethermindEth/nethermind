// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXBatchDerivationTests
{
    private const ulong RollupId = 1;
    private const ulong ChainId = 6290;
    private const ulong StartingNonce = 7;
    private static readonly EezSettlementContext Context = new(RollupId, ChainId, Address.Zero, default, 2);
    private static readonly EezChainRules Rules = EezChainRules.RollupX(new RollupXSchedule(2, 6,
        new RollupXL1Reference(1, 0, TestItem.KeccakA, 0, TestItem.KeccakB), 0));
    private static readonly StreamMessage.Call Incoming = new(RollupId, TestItem.AddressA, TestItem.AddressB, 3, 0, new byte[] { 0x11, 0x22 });
    private static readonly StreamMessage.Return Success = new(true, new byte[] { 0xab });

    [TestCase(2, TestName = "Derive_TwoIdenticalInboundCalls_PreservesBothDeliveries")]
    [TestCase(3, TestName = "Derive_ThreeIdenticalInboundCalls_PreservesAllDeliveries")]
    public void Derive_RepeatedInboundCalls_PreservesOrderAndNonces(int count)
    {
        List<StreamMessage> messages = [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty)];
        for (int i = 0; i < count; i++)
        {
            messages.Add(Incoming);
            messages.Add(Success);
        }
        messages.Add(new StreamMessage.Finish());

        DerivedBlock[] blocks = Derive(messages.ToArray());

        Assert.That(blocks, Has.Length.EqualTo(6), "the full Sync interval must remain present");
        Assert.That(blocks[^1].Transactions, Has.Length.EqualTo(count), "identical calls are distinct canonical deliveries");
        for (int i = 0; i < count; i++)
        {
            AssertDelivery(blocks[^1].Transactions[i], StartingNonce + (ulong)i, Incoming, Success);
        }
    }

    [Test]
    public void Derive_SeparateInboundTransactions_KeepsResultsAndNoncesInOrder()
    {
        StreamMessage.Call other = Incoming with { To = TestItem.AddressC, Value = 5, Data = new byte[] { 0x33 } };
        StreamMessage.Return otherResult = Success with { ReturnData = new byte[] { 0xcd } };
        StreamMessage[] messages = [
            .. Inbound(Incoming, Success, Incoming, otherResult),
            .. Inbound(other, Success),
        ];

        byte[][] transactions = Derive(messages)[^1].Transactions;

        Assert.That(transactions, Has.Length.EqualTo(3), "source transaction boundaries must not drop or repeat a delivery");
        AssertDelivery(transactions[0], StartingNonce, Incoming, Success);
        AssertDelivery(transactions[1], StartingNonce + 1, Incoming, otherResult);
        AssertDelivery(transactions[2], StartingNonce + 2, other, Success);
    }

    [Test]
    public void Derive_SelectedRepeatedInboundEffect_AppendsOnlyTheSelectedCall()
    {
        StreamMessage.Return otherResult = Success with { ReturnData = new byte[] { 0xcd } };

        DerivedBlock[] blocks = Derive(Inbound(Incoming, Success, Incoming, otherResult), new ProducingSlice(1, 1), resumed: true);

        Assert.That(blocks, Has.Length.EqualTo(1), "resumption rebuilds only the terminal Sync block");
        Assert.That(blocks[0].Transactions, Has.Length.EqualTo(1), "a producing slice must not replay its skipped delivery");
        AssertDelivery(blocks[0].Transactions[0], StartingNonce + 1, Incoming, otherResult);
    }

    [Test]
    public void Derive_FailedInboundLookupBetweenCalls_EmitsOnlySuccessfulDeliveries()
    {
        StreamMessage.Return lookup = new(false, new byte[] { 0xef });

        byte[][] transactions = Derive(Inbound(Incoming, Success, Incoming, lookup, Incoming, Success))[^1].Transactions;

        Assert.That(transactions, Has.Length.EqualTo(2), "a failed inbound lookup must not become an executing system transaction");
        AssertDelivery(transactions[0], StartingNonce, Incoming, Success);
        AssertDelivery(transactions[1], StartingNonce + 1, Incoming, Success);
    }

    [Test]
    public void Decode_OutboundAfterFailedInboundLookup_RejectsInvalidOrder()
    {
        StreamMessage[] messages = [
            .. Inbound(Incoming, new StreamMessage.Return(false, ReadOnlyMemory<byte>.Empty)),
            .. Outbound(Incoming with { ToChain = 0 }, Success),
        ];

        Assert.That(() => Decode(messages), Throws.TypeOf<EezSettlementException>(),
            "an inbound lookup still establishes the inbound phase even without an executing delivery");
    }

    [Test]
    public void Decode_MultipleOutboundCalls_RejectsDuplicatingTheUserTransaction()
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0 };

        Assert.That(() => Decode(Outbound(outgoing, Success, outgoing, Success)), Throws.TypeOf<EezSettlementException>(),
            "outbound groups need one combined table and one user transaction, which this follower does not yet lower");
    }

    [TestCaseSource(nameof(UnsupportedInbound))]
    public void Decode_UnsupportedInboundShape_Rejects(StreamMessage[] calls) =>
        Assert.That(() => Decode(Inbound(calls)), Throws.TypeOf<EezSettlementException>(),
            "accepting sequential flat calls must not admit unsupported semantics");

    private static DecodedBatch Decode(StreamMessage[] messages)
    {
        byte[] operations = OperationsPayload.Encode([new OperationsRun(6, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        return new RollupXBatchDerivation(Context, Rules).Decode(NativeStream.Encode(RollupId, operations, messages));
    }

    private static DerivedBlock[] Derive(StreamMessage[] messages, ProducingSlice? applied = null, bool resumed = false)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        DerivationInputs inputs = new(StartingNonce, TestItem.KeccakB, parent, new L1Anchor(1, TestItem.KeccakA, 12, TestItem.KeccakB), resumed);
        return Decode(messages).Derive(applied ?? new ProducingSlice(0, int.MaxValue), inputs);
    }

    private static void AssertDelivery(byte[] encoded, ulong nonce, StreamMessage.Call call, StreamMessage.Return result)
    {
        Transaction transaction = TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping)!;
        ValueHash256 hash = CrossChainCallHash.Compute(false, call.From, 0, call.To, RollupId, call.Value, 0, call.Data.Span);
        CrossChainCall incoming = new(0, false, 0, call.From, 0, call.To, call.Value, call.Data.ToArray());
        L2ExecutionEntry entry = new(hash, [incoming], [], RollingHash.SingleL2Call(hash, true, result.ReturnData.Span), true, result.ReturnData.ToArray());
        byte[] expected = EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([entry], []));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Type, Is.EqualTo(EezConstants.SystemTxType), "each inbound effect is an unsigned native transaction");
            Assert.That(transaction.ChainId, Is.EqualTo(ChainId), "the native transaction must retain its L2 chain identity");
            Assert.That(transaction.To, Is.EqualTo(EezConstants.RollupXEezl2Address), "RollupX deliveries target only the native manager");
            Assert.That(transaction.Nonce, Is.EqualTo(nonce), "each delivery consumes the next system nonce without resetting at a source boundary");
            Assert.That(transaction.Value, Is.EqualTo(call.Value), "each delivery funds its own committed incoming call");
            Assert.That(transaction.Data.ToArray(), Is.EqualTo(expected), "call identity and output must bind to this delivery in order");
        }
    }

    private static StreamMessage[] Inbound(params StreamMessage[] calls) =>
        [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), .. calls, new StreamMessage.Finish()];

    private static StreamMessage[] Outbound(params StreamMessage[] calls)
    {
        byte[] user = TxDecoder.Instance.Encode(Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(ChainId)
            .WithSignature(new Signature(1, 2, 27)).TestObject, RlpBehaviors.SkipTypedWrapping).Bytes;
        return [new StreamMessage.Initiate(RollupId, user), .. calls, new StreamMessage.Finish()];
    }

    private static IEnumerable<TestCaseData> UnsupportedInbound()
    {
        yield return new TestCaseData(new object[] { Array.Empty<StreamMessage>() }).SetName("Decode_EmptyInboundBracket_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming, Incoming, Success, Success } }).SetName("Decode_NestedInboundCalls_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            new StreamMessage.StaticCall(RollupId, Incoming.From, Incoming.To, 0, Incoming.Data), Success,
        } }).SetName("Decode_StaticInboundCall_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] {
            new StreamMessage.Snapshot(), Incoming, Success, new StreamMessage.Revert(),
        } }).SetName("Decode_RolledBackInboundCall_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming with { Gas = 1 }, Success } }).SetName("Decode_InboundCallWithGas_Rejects");
        yield return new TestCaseData(new object[] { new StreamMessage[] { Incoming with { ToChain = 2 }, Success } }).SetName("Decode_InboundCallToOtherRollup_Rejects");
    }
}
