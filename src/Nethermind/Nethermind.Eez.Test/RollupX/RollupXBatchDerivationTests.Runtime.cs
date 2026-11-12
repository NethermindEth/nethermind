// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXBatchDerivationTests
{
    [Test]
    public void Derive_RolledBackSourceThenDifferentCall_RetainsBothPrograms()
    {
        StreamMessage.Call first = Incoming with { ToChain = 0 };
        StreamMessage.Call second = first with { To = TestItem.AddressC };
        StreamMessage[] messages = Outbound(new StreamMessage.Snapshot(), first, Success, new StreamMessage.Revert(), second, Success);
        ValueHash256 a = CallHash(first, RollupId), b = CallHash(second, RollupId);
        L2ExecutionEntry[] entries = [new(a, [], [], RollingHash.SeedL2(a), true, Success.ReturnData.ToArray()),
            new(b, [], [], RollingHash.SeedL2(b), true, Success.ReturnData.ToArray())];

        Transaction transaction = DecodeTransaction(Derive(messages)[^1].Transactions[0]);

        Assert.That(transaction.Data.ToArray(), Is.EqualTo(EezCalldata.EncodeLoadExecutionTable(new(entries, []))),
            "a restored runtime cursor searches by call identity instead of overwriting a different source program");
    }

    [Test]
    public void Derive_FailedInboundWithCallbacks_RetainsOnlyAuthenticatedLookupProgram([Values] OperationsVersion version)
    {
        StreamMessage.Call callback = Incoming with { ToChain = 0, Value = 7 };
        StreamMessage.Return failure = new(false, new byte[] { 0xdd });
        StreamMessage[] messages = Inbound(Incoming, callback, Success, failure);
        NativeCallTree tree = InboundTree(messages);
        ValueHash256 hash = CallHash(Incoming, 0), callbackHash = CallHash(callback, RollupId);
        ValueHash256 open = RollingHash.CallBegin(RollingHash.SeedL2(hash), hash);
        ExpectedCall row = new(Position(callbackHash, open), [], default, true, Success.ReturnData.ToArray());
        L2ExecutionEntry expected = new(hash, [Encoded(Incoming, 0)], [row],
            RollingHash.CallEnd(open, false, failure.ReturnData.Span), true, []);

        L2ExecutionEntry lookup = new NativeExecutionTables(tree, RollupId, version: version).Lookup(tree.Roots[0]);
        DerivedBlock[] canonical = Derive(messages, version: version);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EezCalldata.EncodeExecuteIncomingCrossChainCall(new([lookup], [])),
                Is.EqualTo(EezCalldata.EncodeExecuteIncomingCrossChainCall(new([expected], []))), "a failed target still needs a complete verified delivery trace");
            Assert.That(canonical[^1].Transactions, Is.Empty, "lookup execution never becomes a canonical delivery");
            Assert.That(NativeValue.Delta(tree, tree.Calls[0]).IsZero, Is.True, "a failed parent rolls back all descendant value transfers");
        }
    }

    [Test]
    public void Derive_RecursiveStaticRead_PreservesCallbacksAndPendingRows([Values] OperationsVersion version)
    {
        StreamMessage.StaticCall read = new(0, TestItem.AddressB, TestItem.AddressC, 0, new byte[] { 0x44 });
        StreamMessage.StaticCall callback = new(RollupId, TestItem.AddressC, TestItem.AddressB, 0, new byte[] { 0x55 });
        StreamMessage[] messages = Inbound(Incoming,
            read, callback, Success, Success,
            read, callback, read, callback, Success, Success, Success, Success, Success);
        ValueHash256 root = CallHash(Incoming, 0);
        ValueHash256 open = RollingHash.CallBegin(RollingHash.SeedL2(root), root);
        ValueHash256 hash = CrossChainCallHash.Compute(true, read.From, RollupId, read.To, 0, 0, 0, read.Data.Span);
        CrossChainCall encoded = new(0, true, 0, callback.From, 0, callback.To, 0, callback.Data.ToArray());
        ExpectedCall row = new(Position(hash, open), [encoded], RollingHash.StaticResult(default, true, Success.ReturnData.Span), true, Success.ReturnData.ToArray());
        ExpectedCall[] rows = version == OperationsVersion.V1 ? [row, row] : [row, row, row];
        L2ExecutionEntry expected = new(root, [Encoded(Incoming, 0)], rows,
            RollingHash.CallEnd(open, true, Success.ReturnData.Span), true, Success.ReturnData.ToArray());

        Transaction transaction = DecodeTransaction(Derive(messages, version: version)[^1].Transactions[0]);

        Assert.That(transaction.Data.ToArray(), Is.EqualTo(EezCalldata.EncodeExecuteIncomingCrossChainCall(new([expected], []))),
            "a reused row becomes pending again; a recursive read cannot reuse it until its frame completes");
    }

    [Test]
    public void Derive_ReusedFailedCallbackProgram_DoesNotAppendItsCalls([Values] OperationsVersion version, [Values] bool changeCallback)
    {
        StreamMessage.Call outgoing = Incoming with { ToChain = 0, Value = 5 };
        StreamMessage.Call returning = Incoming with { Value = 7 };
        StreamMessage.Return failure = new(false, new byte[] { 0xdd });
        StreamMessage[] messages = Inbound(Incoming, outgoing, returning, Success, failure,
            outgoing, changeCallback ? returning with { To = TestItem.AddressC } : returning, Success, failure, Success);
        if (changeCallback)
        {
            Assert.That(() => Decode(messages, version), Throws.TypeOf<EezSettlementException>(), "equal results cannot authorize a different callback program");
            return;
        }
        ValueHash256 root = CallHash(Incoming, 0), hash = CallHash(outgoing, RollupId);
        ValueHash256 open = RollingHash.CallBegin(RollingHash.SeedL2(root), root);
        ValueHash256 nested = RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.NestedBegin(open, hash), CallHash(returning, 0)), true, Success.ReturnData.Span);
        ExpectedCall row = new(Position(hash, open), [Encoded(returning, 0)], nested, false, failure.ReturnData.ToArray());
        L2ExecutionEntry expected = new(root, [Encoded(Incoming, 0)], [row],
            RollingHash.CallEnd(open, true, Success.ReturnData.Span), true, Success.ReturnData.ToArray());

        Transaction transaction = DecodeTransaction(Derive(messages, version: version)[^1].Transactions[0]);

        Assert.That(transaction.Data.ToArray(), Is.EqualTo(EezCalldata.EncodeExecuteIncomingCrossChainCall(new([expected], []))),
            "the second invocation must verify the cached callback program without duplicating its incoming calls");
    }

    [Test]
    public void Materialize_NestedRollbackAtSameStart_DoesNotWrapAnAlreadyRevertedPrefix()
    {
        StreamMessage.Call first = Incoming with { ToChain = 0 };
        StreamMessage.Call second = first with { Data = new byte[] { 0x55 } };
        StreamMessage[] messages = Inbound(Incoming, new StreamMessage.Snapshot(), new StreamMessage.Snapshot(),
            first, Success, new StreamMessage.Revert(), second, Success, new StreamMessage.Revert(), Success);
        NativeCallTree tree = InboundTree(messages);
        RollupUpdate update = new(RollupId, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, 0);
        NativeL1Tables l1 = new(new RollupUpdate[] { update }, RollupId, update.CurrentRoot);
        ValueHash256 hash = CallHash(Incoming, 0);
        ValueHash256 rolling = RollingHash.SeedL1(update, hash);
        foreach (StreamMessage.Call call in new[] { first, second })
            rolling = RollingHash.CallEnd(RollingHash.CallBegin(rolling, CallHash(call, RollupId)), true, Success.ReturnData.Span);
        ExecutionEntry expected = new([update], hash,
            [Encoded(first, RollupId) with { RevertNextNCalls = 1 }, Encoded(second, RollupId) with { RevertNextNCalls = 1 }],
            [], rolling, RollupId, true, Success.ReturnData.ToArray());

        _ = new NativeExecutionTables(tree, RollupId, l1, OperationsVersion.V1).Inbound();
        l1.Complete();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(EezCalldata.EntryHash(l1.Entries[0]), Is.EqualTo(EezCalldata.EntryHash(expected)), "inner rollback owns its first marker; the outer scope starts at the remaining suffix");
            Assert.That(l1.EntryEvents[0], Is.Empty, "both rollback scopes discard their emitted outbound events");
            Assert.That(NativeValue.Delta(tree), Is.EqualTo(EtherDelta.Credit(Incoming.Value)), "only the root's incoming value remains after both nested scopes close");
        }
    }

    [Test]
    public void Derive_StaticSourceLookup_RejectsConflictingResults([Values] OperationsVersion version, [Values] bool changeStatus)
    {
        StreamMessage.StaticCall read = new(0, Incoming.From, Incoming.To, 0, new byte[] { 0x44 });
        StreamMessage.Return conflict = changeStatus ? Success with { Success = false } : Success with { ReturnData = new byte[] { 0xdd } };
        StreamMessage[] baseline = Outbound(read, Success, read, Success);
        StreamMessage[] changed = Outbound(read, Success, read, conflict);

        Assert.That(() => Decode(baseline, version), Throws.Nothing);
        Assert.That(() => Decode(changed, version), Throws.TypeOf<EezSettlementException>(),
            "static source lookup identity excludes the claimed result, so contradictory outcomes must fail instead of creating another row");
    }

    private static NativeCallTree InboundTree(StreamMessage[] messages)
    {
        int index = 1;
        return NativeCallTree.Read(messages, ref index, 0, RollupId);
    }
}
