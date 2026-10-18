// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXSettlementVerifierTests
{
    [Test]
    public void Verify_NestedInbound_BindsBothTablesValueAndEvent([Values] bool reverted)
    {
        Fixture fixture = NestedInbound(reverted);

        Assert.That(Verify(fixture), Is.Empty, "mutable nested execution needs no synthetic observation");
    }

    [Test]
    public void Materialize_NestedRevertWithReturningChild_ClosesAtEnclosingCallForFollower()
    {
        Fixture fixture = NestedInbound(true);
        int index = 1;
        NativeCallTree tree = NativeCallTree.Read(fixture.Stream.Messages, ref index, 0, 1);
        L2ExecutionEntry[] entries = new NativeExecutionTables(tree, 1).Inbound();

        Assert.That(EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall(entries, [])),
            Is.EqualTo(fixture.Result.Block.Transactions[1].Data.ToArray()), "the default follower must retain the independently assembled rollback hash");
    }

    [TestCase("delta", TestName = "Verify_ChangedNestedInboundDelta_Rejects")]
    [TestCase("funding", TestName = "Verify_ChangedNestedInboundFunding_Rejects")]
    [TestCase("calldata", TestName = "Verify_ChangedNestedInboundCalldata_Rejects")]
    [TestCase("checkpoint", TestName = "Verify_ChangedNestedInboundCheckpoint_Rejects")]
    [TestCase("event-hash", TestName = "Verify_ChangedNestedInboundEventHash_Rejects")]
    [TestCase("event-position", TestName = "Verify_ChangedNestedInboundEventPosition_Rejects")]
    [TestCase("missing-event", TestName = "Verify_ChangedNestedInboundMissingEvent_Rejects")]
    [TestCase("extra-event", TestName = "Verify_ChangedNestedInboundExtraEvent_Rejects")]
    [TestCase("missing-row", TestName = "Verify_ChangedNestedInboundMissingRow_Rejects")]
    public void Verify_ChangedNestedInbound_Rejects(string change)
    {
        Fixture fixture = NestedInbound(false);
        Assert.That(Verify(fixture), Is.Empty, "the exact independently assembled baseline must pass before mutation");
        switch (change)
        {
            case "delta": ChangeDelta(fixture, 8); break;
            case "funding": fixture.Result.Block.Transactions[1].Value = 15; break;
            case "calldata":
                byte[] calldata = fixture.Result.Block.Transactions[1].Data.ToArray();
                calldata[^1] ^= 1;
                fixture.Result.Block.Transactions[1].Data = calldata;
                break;
            case "checkpoint": fixture.Result.Checkpoints[1] = fixture.Result.Checkpoints[1] with { BlockHash = TestItem.KeccakD }; break;
            case "event-hash": fixture.Result.Receipts[1].Logs![0].Topics[1] = TestItem.KeccakD; break;
            case "event-position":
                fixture.Result.Receipts[0].Logs = fixture.Result.Receipts[1].Logs;
                fixture.Result.Receipts[1].Logs = [];
                break;
            case "missing-event": fixture.Result.Receipts[1].Logs = []; break;
            case "extra-event": fixture.Result.Receipts[1].Logs = [.. fixture.Result.Receipts[1].Logs!, fixture.Result.Receipts[1].Logs![0]]; break;
            case "missing-row": fixture.Batch.Entries[1] = fixture.Batch.Entries[1] with { ExpectedCalls = [] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        Assert.That(() => Verify(fixture), Throws.TypeOf<EezSettlementException>(), "every independent trust boundary must reject the changed claim");
    }

    [Test]
    public void Verify_RevertedNestedCallbackEvent_Rejects()
    {
        Fixture fixture = NestedInbound(true);
        Assert.That(Verify(fixture), Is.Empty);
        fixture.Result.Receipts[1].Logs = [Event(Hash(Call(0, 7, 0x22), 1))];

        Assert.That(() => Verify(fixture), Throws.TypeOf<EezSettlementException>(), "a reverted callback cannot emit a committed event");
    }

    [Test]
    public void Verify_FailedOutboundValue_RequiresZeroDelta()
    {
        Fixture fixture = FailedOutbound();
        Assert.That(Verify(fixture), Is.Empty, "the failed positive-value proxy call commits no transfer or event");
        ChangeDelta(fixture, -7);

        Assert.That(() => Verify(fixture), Throws.TypeOf<EezSettlementException>(), "a proposer cannot turn a failed recorded value into a signed debit");
    }

    [Test]
    public void Verify_InboundRootRange_CannotBeHiddenByOffsettingValues()
    {
        UInt256 limit = UInt256.One << 191;
        Assert.That(Verify(NestedInbound(false, limit - 1, limit - 1)), Is.Empty);

        Assert.That(() => Verify(NestedInbound(false, limit, limit)), Throws.TypeOf<EezSettlementException>().With.Message.Contains("Inbound root value"));
    }

    [Test]
    public void Verify_FailedOutboundRoot_StillRequiresRecordedValueRange()
    {
        UInt256 limit = UInt256.One << 191;
        Assert.That(Verify(FailedOutbound(limit)), Is.Empty);

        Assert.That(() => Verify(FailedOutbound(limit + 1)), Throws.TypeOf<EezSettlementException>().With.Message.Contains("Outbound recorded call value"));
    }

    [Test]
    public void Verify_ReservedNestedL2Source_Rejects()
    {
        Assert.That(Verify(NestedInbound(false)), Is.Empty);

        Assert.That(() => Verify(NestedInbound(false, childSource: EezConstants.SystemAddress)),
            Throws.TypeOf<EezSettlementException>().With.Message.Contains("reserved L2 system source"));
    }

    [Test]
    public void Verify_ReservedOutboundRootSource_Rejects()
    {
        Assert.That(Verify(FailedOutbound()), Is.Empty);

        Assert.That(() => Verify(FailedOutbound(source: EezConstants.SystemAddress)),
            Throws.TypeOf<EezSettlementException>().With.Message.Contains("reserved L2 system source"));
    }

    [Test]
    public void Verify_OutboundCallbacks_RequiresZeroLoadValue()
    {
        Assert.That(Verify(NestedOutbound(0)), Is.Empty, "zero-value callbacks match both the published builder and signer");

        Assert.That(() => Verify(NestedOutbound(7)), Throws.TypeOf<EezSettlementException>().With.Message.Contains("Outbound load must carry zero native value"),
            "the published builder funds positive callbacks, but the signer refuses funded loads; do not silently choose a third behavior");
    }

    private static Fixture NestedInbound(bool reverted, UInt256? rootValue = null, UInt256? childValue = null, Address? childSource = null)
    {
        StreamMessage.Call root = Call(1, rootValue ?? 11, 0x11);
        StreamMessage.Call child = Call(0, childValue ?? 7, 0x22) with { From = childSource ?? TestItem.AddressA };
        StreamMessage.Call grandchild = Call(1, 3, 0x33);
        ValueHash256 rootHash = Hash(root, 0);
        ValueHash256 childHash = Hash(child, 1);
        ValueHash256 grandchildHash = Hash(grandchild, 0);
        RollupUpdate update = new(1, TestItem.KeccakB.ValueHash256, default, new SignedInt256(reverted ? root.Value : root.Value - child.Value + 3));
        ValueHash256 l2Start = RollingHash.CallBegin(RollingHash.SeedL2(rootHash), rootHash);
        ValueHash256 l1Start = RollingHash.CallBegin(RollingHash.SeedL1(update, rootHash), childHash);
        ValueHash256 l2Nested = RollingHash.NestedBegin(l2Start, childHash);
        l2Nested = RollingHash.CallEnd(RollingHash.CallBegin(l2Nested, grandchildHash), true, [0x33]);
        ValueHash256 l2Final = RollingHash.CallEnd(reverted ? l2Start : RollingHash.NestedEnd(l2Nested), true, [0x11]);
        ValueHash256 l1Final = RollingHash.CallEnd(RollingHash.NestedEnd(RollingHash.NestedBegin(l1Start, grandchildHash)), true, [0x22]);
        ExpectedCall l2Row = new(Position(childHash, l2Start), [Encoded(grandchild, 0)], default, true, [0x22]);
        L2ExecutionEntry l2 = new(rootHash, [Encoded(root, 0)], [l2Row], l2Final, true, [0x11]);
        ExpectedCall l1Row = new(Position(grandchildHash, l1Start), [], default, true, [0x33]);
        CrossChainCall l1Call = Encoded(child, 1) with { RevertNextNCalls = reverted ? (ushort)1 : (ushort)0 };
        ExecutionEntry l1 = new([update], rootHash, [l1Call], [l1Row], l1Final, 1, true, [0x11]);
        Transaction ordinary = User();
        Transaction delivery = System(root.Value + 3, EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([l2], [])));
        StreamMessage[] messages = reverted
            ? [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), root, new StreamMessage.Snapshot(), child, grandchild,
                Result(0x33), Result(0x22), new StreamMessage.Revert(), Result(0x11), new StreamMessage.Finish()]
            : [new StreamMessage.Initiate(0, ReadOnlyMemory<byte>.Empty), root, child, grandchild,
                Result(0x33), Result(0x22), Result(0x11), new StreamMessage.Finish()];
        return Assemble([ordinary, delivery], [ordinary], l1, messages, reverted ? [] : [Event(childHash)], 1);
    }

    private static Fixture NestedOutbound(int callbackValue)
    {
        StreamMessage.Call root = Call(0, 11, 0x11);
        StreamMessage.Call child = Call(1, (UInt256)(uint)callbackValue, 0x22);
        StreamMessage.Call grandchild = Call(0, 3, 0x33);
        ValueHash256 rootHash = Hash(root, 1);
        ValueHash256 childHash = Hash(child, 0);
        ValueHash256 grandchildHash = Hash(grandchild, 1);
        RollupUpdate update = new(1, TestItem.KeccakB.ValueHash256, default, new SignedInt256(callbackValue - 14));
        ValueHash256 l1Start = RollingHash.CallBegin(RollingHash.SeedL1(update, default), rootHash);
        ValueHash256 l2Start = RollingHash.CallBegin(RollingHash.SeedL2(rootHash), childHash);
        ValueHash256 l1Nested = RollingHash.NestedBegin(l1Start, childHash);
        ValueHash256 l1Final = RollingHash.CallEnd(RollingHash.NestedEnd(
            RollingHash.CallEnd(RollingHash.CallBegin(l1Nested, grandchildHash), true, [0x33])), true, [0x11]);
        ValueHash256 l2Final = RollingHash.CallEnd(RollingHash.NestedEnd(RollingHash.NestedBegin(l2Start, grandchildHash)), true, [0x22]);
        ExpectedCall l2Row = new(Position(grandchildHash, l2Start), [], default, true, [0x33]);
        L2ExecutionEntry l2 = new(rootHash, [Encoded(child, 0)], [l2Row], l2Final, true, [0x11]);
        ExpectedCall l1Row = new(Position(childHash, l1Start), [Encoded(grandchild, 1)], default, true, [0x22]);
        ExecutionEntry l1 = new([update], default, [Encoded(root, 1)], [l1Row], l1Final, 1, true, []);
        Transaction user = User();
        Transaction load = System((UInt256)(uint)callbackValue, EezCalldata.EncodeLoadExecutionTable(new ExecutionTable([l2], [])));
        StreamMessage[] messages = [new StreamMessage.Initiate(1, TxDecoder.Instance.Encode(user, RlpBehaviors.SkipTypedWrapping).Bytes),
            root, child, grandchild, Result(0x33), Result(0x22), Result(0x11), new StreamMessage.Finish()];
        return Assemble([load, user], [], l1, messages, [Event(rootHash), Event(grandchildHash)], 2);
    }

    private static Fixture FailedOutbound(UInt256? value = null, Address? source = null)
    {
        StreamMessage.Call root = Call(0, value ?? 7, 0x44) with { From = source ?? TestItem.AddressA };
        ValueHash256 hash = Hash(root, 1);
        L2ExecutionEntry l2 = new(hash, [], [], RollingHash.SeedL2(hash), false, [0xef]);
        RollupUpdate update = new(1, TestItem.KeccakB.ValueHash256, default, SignedInt256.Zero);
        ValueHash256 rolling = RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.SeedL1(update, default), hash), false, [0xef]);
        ExecutionEntry l1 = new([update], default, [Encoded(root, 1)], [], rolling, 1, true, []);
        Transaction user = User();
        Transaction load = System(0, EezCalldata.EncodeLoadExecutionTable(new ExecutionTable([l2], [])));
        StreamMessage[] messages = [new StreamMessage.Initiate(1, TxDecoder.Instance.Encode(user, RlpBehaviors.SkipTypedWrapping).Bytes),
            root, new StreamMessage.Return(false, new byte[] { 0xef }), new StreamMessage.Finish()];
        return Assemble([load, user], [], l1, messages, [], 2);
    }

    private static Fixture Assemble(Transaction[] transactions, Transaction[] ordinary, ExecutionEntry effect,
        StreamMessage[] messages, LogEntry[] events, uint immediate)
    {
        Block block = Build.A.Block.WithParentHash(TestItem.KeccakA).WithBeneficiary(TestItem.AddressA)
            .WithExtraData([0x42]).WithTransactions(transactions).TestObject;
        RollupUpdate anchor = new(1, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, SignedInt256.Zero);
        ExecutionEntry first = new([anchor], default, [], [], RollingHash.SeedL1(anchor, default), 1, true, []);
        effect = effect with { RollupUpdates = [effect.RollupUpdates[0] with { NewRoot = block.Hash!.ValueHash256 }] };
        PostBatch batch = new([], [first, effect], [], immediate, 0, [], [], [], [], [], 1, false);
        TxReceipt[] receipts = [new() { StatusCode = StatusCode.Success, Logs = [] }, new() { StatusCode = StatusCode.Success, Logs = events }];
        EezTransactionCheckpoint[] checkpoints = [new(TransactionIndex: ordinary.Length - 1, StateRoot: TestItem.KeccakC, BlockHash: TestItem.KeccakB),
            new(TransactionIndex: 1, StateRoot: TestItem.KeccakD, BlockHash: block.Hash!)];
        EezStatelessBlockResult result = new(block, block.Header, receipts, checkpoints);
        ReadOnlyMemory<byte>[] encoded = new ReadOnlyMemory<byte>[ordinary.Length];
        for (int i = 0; i < ordinary.Length; i++) encoded[i] = TxDecoder.Instance.Encode(ordinary[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        byte[] operations = OperationsPayload.Encode([new OperationsRun(1, TestItem.AddressA, new byte[] { 0x42 }, encoded)]);
        return new Fixture(batch, new NativeStream(operations, messages), result);
    }

    private static void ChangeDelta(Fixture fixture, int delta)
    {
        ExecutionEntry effect = fixture.Batch.Entries[1];
        fixture.Batch.Entries[1] = effect with { RollupUpdates = [effect.RollupUpdates[0] with { EtherDelta = new SignedInt256(delta) }] };
    }

    private static RollupXObservationClaim[] Verify(Fixture fixture) =>
        RollupXSettlementVerifier.Verify(fixture.Batch, fixture.Stream, [fixture.Result], 1);

    private static StreamMessage.Call Call(ulong destination, UInt256 value, byte data) =>
        new(destination, TestItem.AddressA, TestItem.AddressB, value, 0, new byte[] { data });

    private static StreamMessage.Return Result(byte output) => new(true, new byte[] { output });

    private static CrossChainCall Encoded(StreamMessage.Call call, ulong source) =>
        new(0, false, 0, call.From, source, call.To, call.Value, call.Data.ToArray());

    private static ValueHash256 Hash(StreamMessage.Call call, ulong source) =>
        CrossChainCallHash.Compute(false, call.From, source, call.To, call.ToChain, call.Value, 0, call.Data.Span);

    private static ValueHash256 Position(ValueHash256 call, ValueHash256 rolling)
    {
        Span<byte> input = stackalloc byte[64];
        call.Bytes.CopyTo(input);
        rolling.Bytes.CopyTo(input[32..]);
        return ValueKeccak.Compute(input);
    }

    private static Transaction User() => Build.A.Transaction.WithNonce(7).WithChainId(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static Transaction System(UInt256 value, byte[] data) => Build.A.Transaction.WithType(EezConstants.SystemTxType)
        .WithSenderAddress(EezConstants.SystemAddress).WithTo(EezConstants.RollupXEezl2Address).WithValue(value).WithData(data).TestObject;

    private static LogEntry Event(ValueHash256 hash)
    {
        byte[] data = new byte[160];
        data[63] = 128;
        return new LogEntry(EezConstants.RollupXEezl2Address, data, [OutboundEvent.Signature, new Hash256(hash.Bytes), Keccak.Zero]);
    }

    private sealed record Fixture(PostBatch Batch, NativeStream Stream, EezStatelessBlockResult Result);
}
