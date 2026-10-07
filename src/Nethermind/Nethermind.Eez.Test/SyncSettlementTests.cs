// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Evm;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.Eez.Test.TestWords;
using static Nethermind.Eez.Test.SyncSettlementFixture;

namespace Nethermind.Eez.Test;

public class SyncSettlementTests
{
    [Test]
    public void Encode_SystemTransaction_IsTheCanonicalWireFormat() =>
        Assert.That(SyncBlock.Encode(1, 0, UInt256.Zero, [1, 2, 3, 4]),
            Is.EqualTo(Bytes.FromHexString("0x76dd0180944200000000000000000000000000000000000007808401020304")));

    [Test]
    public void BuildTransactions_MixedEffects_LoadsEachOutboundBeforeItsUserThenDeliversInbound()
    {
        SyncSettlementFixture fixture = new();
        Transaction load = Decode(fixture.SyncTransactions[0]);
        Transaction delivery = Decode(fixture.SyncTransactions[2]);
        ExecutionTable table = EezCalldata.DecodeLoadExecutionTable(load.Data.Span);
        ValueHash256 proxy = CrossChainCallHash.Compute(false, L2Contract, RollupId, L1Target, 0, 3, 0, fixture.OutboundCall.Data);

        Assert.That(fixture.SyncTransactions, Has.Length.EqualTo(3));
        Assert.That(fixture.SyncTransactions[1], Is.EqualTo(fixture.UserTransaction), "the user transaction consumes the load right before it");
        Assert.That((load.Nonce, delivery.Nonce), Is.EqualTo((StartingNonce, StartingNonce + 1)), "system nonces run in transaction order");
        Assert.That((load.Value, delivery.Value), Is.EqualTo((UInt256.Zero, (UInt256)7)), "only a delivery carries value");
        Assert.That(load.ChainId, Is.EqualTo(ChainId));
        Assert.That(table.Entries, Has.Length.EqualTo(1), "one load stages one entry");
        Assert.That((table.Entries[0].ProxyEntryHash, table.Entries[0].RollingHash), Is.EqualTo((proxy, RollingHash.SeedL2(proxy))),
            "the staged entry is keyed by the outbound call");
        Assert.That((table.Entries[0].Success, table.Entries[0].ReturnData), Is.EqualTo((true, fixture.OutboundDaEntry.ReturnData)),
            "the staged entry returns the L1 result to the user transaction");
        Assert.That((table.Entries[0].IncomingCalls, table.Entries[0].ExpectedOutgoingCalls, table.StaticEntries), Is.EqualTo((Array.Empty<CrossChainCall>(),
            Array.Empty<ExpectedCall>(), Array.Empty<L2StaticExecutionEntry>())), "the staged entry re-executes nothing on L2");
        Assert.That(EezCalldata.EncodeEntry(fixture.Observation.DerivedDaEntry), Is.EqualTo(EezCalldata.EncodeEntry(fixture.InboundAction.ToEntry(RollupId))),
            "the delivery re-inspects to the entry derivation rebuilds from its action");
    }

    [TestCaseSource(nameof(UnloweredEntries))]
    public void BuildTransactions_EntryThatCannotBeLowered_Throws(Func<ExecutionEntry, ExecutionEntry> mutate, bool asOutbound)
    {
        SyncSettlementFixture fixture = new();
        ExecutionEntry entry = mutate(asOutbound ? fixture.OutboundDaEntry : fixture.InboundAction.ToEntry(RollupId));

        Assert.Throws<EezSettlementException>(() => SyncBlock.BuildTransactions(asOutbound ? [(entry, fixture.UserTransaction)] : [], asOutbound ? [] : [entry],
            ChainId, RollupId, 0));
    }

    [TestCase(RollupId + 1, 1, TestName = "InboundForAnotherRollup")]
    [TestCase(RollupId, 0, TestName = "InboundWithoutACall")]
    public void BuildTransactions_InboundEntryItCannotDeliver_Throws(ulong destination, int calls)
    {
        ExecutionEntry entry = new SyncSettlementFixture().InboundAction.ToEntry(RollupId);
        entry = entry with { DestinationRollupId = destination, Calls = entry.Calls[..calls] };

        Assert.That(Assert.Throws<EezSettlementException>(() => SyncBlock.BuildTransactions([], [entry], ChainId, RollupId, 0))!.Message,
            Does.Contain("deliver exactly one call"));
    }

    [TestCase(ulong.MaxValue - 1, false, TestName = "LastNonceIsUsable")]
    [TestCase(ulong.MaxValue, true, TestName = "NonceOverflows")]
    public void BuildTransactions_NonceAtTheEnd(ulong startingNonce, bool overflows)
    {
        SyncSettlementFixture fixture = new();
        Action build = () => SyncBlock.BuildTransactions([], [fixture.InboundAction.ToEntry(RollupId), fixture.InboundAction.ToEntry(RollupId)],
            ChainId, RollupId, startingNonce - 1);

        if (overflows)
        {
            Assert.Throws<EezSettlementException>(build);
        }
        else
        {
            Assert.DoesNotThrow(build);
        }
    }

    [TestCase(new bool[0], new int[0], TestName = "NoTransactions")]
    [TestCase(new[] { false, false, false }, new[] { 0, 1, 2 }, TestName = "UserTransactionsEachEndAnEffect")]
    [TestCase(new[] { true, false }, new[] { 1 }, TestName = "LoadPairsWithItsUser")]
    [TestCase(new[] { true, true }, new[] { 0, 1 }, TestName = "DeliveriesStandAlone")]
    [TestCase(new[] { false, true }, new[] { 0, 1 }, TestName = "TrailingDelivery")]
    [TestCase(new[] { true, false, true, false }, new[] { 1, 3 }, TestName = "TwoOutboundPairs")]
    [TestCase(new[] { true }, new[] { 0 }, TestName = "LoneSystemTransaction")]
    public void EffectTransactionsOf_EndsAnEffectAtEachUserAndEachUnpairedSystemTransaction(bool[] system, int[] expected)
    {
        Transaction[] transactions = Array.ConvertAll(system, static s => s
            ? SystemTransactions.Create(1)
            : Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        Assert.That(SettlingBlock.EffectTransactionsOf(Build.A.Block.WithTransactions(transactions).TestObject), Is.EqualTo(expected));
    }

    [Test]
    public void Inspect_SyncBlock_ObservesTheDeliveryAndTheOutboundEvent()
    {
        SyncSettlementFixture fixture = new();

        SettlingBlock observed = SettlingBlock.Inspect(fixture.Settling, fixture.SettlingReceipts(), RollupId);

        Assert.That(observed.SystemTransactions, Is.EqualTo(new[] { true, false, true }));
        Assert.That(observed.EffectTransactions, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(observed.InboundCandidates, Is.EqualTo(new[] { new InboundCandidate(2, fixture.Observation, null, false) }).Using<InboundCandidate>(SameCandidate));
        Assert.That(observed.OutboundEvents, Is.EqualTo(new[] { new OutboundEvent(1, 0, true, EventCallHash, 0) }));
    }

    [Test]
    public void Inspect_RevertedLoad_Throws()
    {
        SyncSettlementFixture fixture = new();

        EezSettlementException e = Assert.Throws<EezSettlementException>(() => SettlingBlock.Inspect(fixture.Settling, fixture.SettlingReceipts(StatusCode.Failure), RollupId))!;

        Assert.That((e.Message.Contains("reverted"), e.PoisonedTransactionIndex), Is.EqualTo((true, (int?)1)), "the user transaction the load stages can be evicted");
    }

    [Test]
    public void Inspect_RevertedSystemTransactionThatIsNotALoad_PoisonsNothing()
    {
        Transaction other = SystemTransactions.Create(ChainId, data: [1, 2, 3, 4]);
        Transaction user = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithTransactions(other, user).TestObject;

        EezSettlementException e = Assert.Throws<EezSettlementException>(() => SettlingBlock.Inspect(block,
            [new TxReceipt { StatusCode = StatusCode.Failure, Logs = [] }, new TxReceipt { StatusCode = StatusCode.Success, Logs = [] }], RollupId))!;

        Assert.That(e.PoisonedTransactionIndex, Is.Null);
    }

    [Test]
    public void Inspect_ReceiptCountDiffers_IsTheCallersFault() =>
        Assert.That(Assert.Throws<EezSettlementException>(() => SettlingBlock.Inspect(new SyncSettlementFixture().Settling, [], RollupId))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));

    [Test]
    public void Inspect_SystemTransactionToAnotherTarget_Throws()
    {
        Transaction misdirected = SystemTransactions.Create(ChainId);
        misdirected.To = L1Target;
        Block block = Build.A.Block.WithTransactions(misdirected).TestObject;

        Assert.That(Assert.Throws<EezSettlementException>(() => SettlingBlock.Inspect(block, [new TxReceipt { StatusCode = StatusCode.Success }], RollupId))!.Message,
            Does.Contain("without being a system transaction to EEZL2"));
    }

    [Test]
    public void Inspect_RevertedDelivery_KeepsItAsAnInvalidCandidate()
    {
        SyncSettlementFixture fixture = new();

        SettlingBlock observed = SettlingBlock.Inspect(fixture.Settling, fixture.SettlingReceipts(deliveryStatus: StatusCode.Failure), RollupId);

        Assert.That(observed.InboundCandidates, Has.Length.EqualTo(1));
        Assert.That((observed.InboundCandidates[0].TransactionIndex, observed.InboundCandidates[0].Observation), Is.EqualTo((2, (InboundObservation?)null)));
        Assert.That(observed.InboundCandidates[0].Error, Does.Contain("reverted"));
    }

    [Test]
    public void Inspect_SystemSenderOnAnOrdinaryTransaction_Throws()
    {
        Transaction impostor = Build.A.Transaction.WithTo(EezConstants.Eezl2Address).TestObject;
        impostor.SenderAddress = EezConstants.SystemAddress;
        Block block = Build.A.Block.WithTransactions(impostor).TestObject;

        Assert.That(Assert.Throws<EezSettlementException>(() => SettlingBlock.Inspect(block, [new TxReceipt { StatusCode = StatusCode.Success }], RollupId))!.Message,
            Does.Contain("without being a system transaction to EEZL2"));
    }


    [Test]
    public void EnsureNoEffects_BlockWithASystemTransaction_Throws() =>
        Assert.Throws<EezSettlementException>(() => SettlingBlock.EnsureNoEffects(new SyncSettlementFixture().Settling, []));

    [Test]
    public void EnsureNoEffects_SystemTypeWithoutTheSystemSender_Throws()
    {
        Transaction system = SystemTransactions.Create(ChainId);
        system.SenderAddress = null;

        Assert.Throws<EezSettlementException>(() => SettlingBlock.EnsureNoEffects(Build.A.Block.WithTransactions(system).TestObject, []));
    }

    [Test]
    public void EnsureNoEffects_SystemSenderOnAnOrdinaryTransaction_Throws()
    {
        Transaction impostor = Build.A.Transaction.TestObject;
        impostor.SenderAddress = EezConstants.SystemAddress;

        Assert.Throws<EezSettlementException>(() => SettlingBlock.EnsureNoEffects(Build.A.Block.WithTransactions(impostor).TestObject, []));
    }

    [Test]
    public void EnsureNoEffects_BlockWithAnOutboundEvent_Throws()
    {
        SyncSettlementFixture fixture = new();

        Assert.Throws<EezSettlementException>(() =>
            SettlingBlock.EnsureNoEffects(fixture.PrecedingBlock, [new TxReceipt { Logs = [EventLog(EventCallHash)] }]));
    }

    [Test]
    public void EnsureNoEffects_OrdinaryBlock_Passes() =>
        Assert.DoesNotThrow(() => SettlingBlock.EnsureNoEffects(new SyncSettlementFixture().PrecedingBlock, [new TxReceipt { Logs = [] }]));

    [TestCaseSource(nameof(EventLogs))]
    public bool Observe_EventLog_DecodesOnlyTheCanonicalEncoding(LogEntry log) =>
        OutboundEvent.Observe([new TxReceipt { Logs = [log] }]) is [{ IsCanonical: true }];

    [Test]
    public void Observe_OtherEmitterOrEvent_IsIgnored()
    {
        LogEntry otherEmitter = EventLog(default, emitter: L1Target);
        LogEntry otherEvent = new(EezConstants.Eezl2Address, [], [Keccak.Zero]);

        Assert.That(OutboundEvent.Observe([new TxReceipt { Logs = [otherEmitter, otherEvent] }]), Is.Empty);
    }

    [Test]
    public void Verify_SyncBlockAndItsPayload_Passes()
    {
        SyncSettlementFixture fixture = new();

        Assert.DoesNotThrow(() => DaVerification.Verify(fixture.Payload(), fixture.Window, fixture.Outbound, fixture.Inbound, ChainId, RollupId));
    }

    [TestCaseSource(nameof(BadPayloads))]
    public void Verify_PayloadThatDoesNotPublishTheWindow_Throws(Func<SyncSettlementFixture, byte[]> payload, string rule)
    {
        SyncSettlementFixture fixture = new();

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            DaVerification.Verify(payload(fixture), fixture.Window, fixture.Outbound, fixture.Inbound, ChainId, RollupId))!.Message, Does.Contain(rule));
    }

    [Test]
    public void Verify_SyncBlockWithOtherNonces_Throws()
    {
        SyncSettlementFixture fixture = new();
        Transaction[] shifted = Array.ConvertAll(fixture.Settling.Transactions, static t => t.IsEezSystemTransaction() ? Shift(t) : t);
        Block settling = Build.A.Block.WithNumber(2).WithBeneficiary(Beneficiary).WithExtraData([9]).WithTransactions(shifted).TestObject;

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            DaVerification.Verify(fixture.Payload(), [fixture.PrecedingBlock, settling], fixture.Outbound, fixture.Inbound, ChainId, RollupId))!.Message,
            Does.Contain("not the one its effects rebuild"));

        static Transaction Shift(Transaction t) => Decode(SyncBlock.Encode(ChainId, t.Nonce == StartingNonce ? t.Nonce : t.Nonce + 1, t.Value, t.Data.ToArray()));
    }

    [Test]
    public void Verify_SyncBlockWithATransactionItsEffectsDoNotRebuild_Throws()
    {
        SyncSettlementFixture fixture = new();
        Transaction extra = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block settling = Build.A.Block.WithNumber(2).WithBeneficiary(Beneficiary).WithExtraData([9]).WithTransactions([.. fixture.Settling.Transactions, extra]).TestObject;

        Assert.That(Assert.Throws<EezSettlementException>(() => DaVerification.Verify(fixture.Payload(settlingPublished: [fixture.UserTransaction, Encode(extra)]),
            [fixture.PrecedingBlock, settling], fixture.Outbound, fixture.Inbound, ChainId, RollupId))!.Message, Does.Contain("but its effects rebuild 3"));
    }

    [Test]
    public void Verify_WindowWithoutEffects_PublishesTheSettlingBlockWhole()
    {
        SyncSettlementFixture fixture = new();
        byte[] payload = DaPayloadCodec.Encode(RollupId, [new DaBlock(Beneficiary, [], [fixture.PrecedingTransaction])], []);

        Assert.DoesNotThrow(() => DaVerification.Verify(payload, [fixture.PrecedingBlock], [], [], ChainId, RollupId));
    }

    [Test]
    public void AuthorizeAll_BoundSyncBlock_AuthorizesBothEffectsAndDerivesTheirDaEntries()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, SettlingBlock observed) = Bound(fixture);

        AuthorizedInbound[] inbound = InboundDelivery.AuthorizeAll(effects, observed.InboundCandidates, RollupId);
        AuthorizedOutbound[] outbound = OutboundCall.AuthorizeAll(effects, observed.OutboundEvents, observed.SystemTransactions, RollupId);

        Assert.That(inbound, Has.Length.EqualTo(1));
        Assert.That((inbound[0].TransactionIndex, inbound[0].Observation.CallHash), Is.EqualTo((2, fixture.Observation.CallHash)));
        Assert.That(outbound, Has.Length.EqualTo(1));
        Assert.That((outbound[0].LoadTransactionIndex, outbound[0].TransactionIndex), Is.EqualTo((0, 1)));
        Assert.That(EezCalldata.EncodeEntry(outbound[0].DerivedDaEntry), Is.EqualTo(EezCalldata.EncodeEntry(fixture.OutboundDaEntry)));
        Assert.DoesNotThrow(() => DaVerification.Verify(fixture.Payload(), fixture.Window, outbound, inbound, ChainId, RollupId),
            "the authorized effects are exactly what the DA check consumes");
    }

    [Test]
    public void AuthorizeAll_InboundClaimWithOtherResult_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, SettlingBlock observed) = Bound(fixture);
        BoundEffect[] tampered = [effects[0], effects[1] with { Entry = effects[1].Entry with { ReturnData = [0xcd] } }];

        Assert.That(Assert.Throws<EezSettlementException>(() => InboundDelivery.AuthorizeAll(tampered, observed.InboundCandidates, RollupId))!.Message,
            Does.Contain("different result"));
    }

    [Test]
    public void AuthorizeAll_OutboundClaimForAnotherCall_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, SettlingBlock observed) = Bound(fixture);
        BoundEffect tampered = effects[0] with { Entry = effects[0].Entry with { Calls = [fixture.OutboundCall with { Data = [9] }] } };

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            OutboundCall.AuthorizeAll([tampered, effects[1]], observed.OutboundEvents, observed.SystemTransactions, RollupId))!.Message, Does.Contain("different call"));
    }

    [Test]
    public void AuthorizeAll_OutboundEffectAtTheFirstTransaction_HasNoLoad()
    {
        (BoundEffect[] effects, _) = Bound(new SyncSettlementFixture());

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            OutboundCall.AuthorizeAll([effects[0] with { TransactionIndex = 0 }], [new OutboundEvent(0, 0, true, EventCallHash, 0)], [false], RollupId))!.Message,
            Does.Contain("no system load"));
    }

    [Test]
    public void AuthorizeAll_UnclaimedDelivery_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, SettlingBlock observed) = Bound(fixture);

        Assert.Throws<EezSettlementException>(() => InboundDelivery.AuthorizeAll(effects[..1], observed.InboundCandidates, RollupId));
    }

    [Test]
    public void AuthorizeAll_InboundEntryWithoutDelivery_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, _) = Bound(fixture);

        Assert.Throws<EezSettlementException>(() => InboundDelivery.AuthorizeAll(effects, [], RollupId));
    }

    [Test]
    public void AuthorizeAll_InvalidDelivery_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, _) = Bound(fixture);

        EezSettlementException e = Assert.Throws<EezSettlementException>(() =>
            InboundDelivery.AuthorizeAll(effects, [new InboundCandidate(2, null, "malformed", false)], RollupId))!;

        Assert.That((e.Message.Contains("malformed"), e.PoisonedEntryIndex), Is.EqualTo((true, (int?)null)), "only a reverted delivery can be evicted");
    }

    [Test]
    public void AuthorizeAll_RevertedDelivery_PoisonsItsEntry()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, _) = Bound(fixture);
        SettlingBlock observed = SettlingBlock.Inspect(fixture.Settling, fixture.SettlingReceipts(deliveryStatus: StatusCode.Failure), RollupId);

        EezSettlementException e = Assert.Throws<EezSettlementException>(() => InboundDelivery.AuthorizeAll(effects, observed.InboundCandidates, RollupId))!;

        Assert.That((e.Failure, e.PoisonedEntryIndex, e.PoisonedTransactionIndex), Is.EqualTo((EezSettlementFailure.Rejected, (int?)2, (int?)null)));
    }

    [Test]
    public void AuthorizeAll_DeliveryAtAnOutboundTransaction_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, _) = Bound(fixture);

        Assert.Throws<EezSettlementException>(() => InboundDelivery.AuthorizeAll(effects, [new InboundCandidate(1, fixture.Observation, null, false)], RollupId));
    }

    [TestCaseSource(nameof(BadEvents))]
    public void AuthorizeAll_EventsThatDoNotMatchTheOutboundEntry_Throw(OutboundEvent[] events, bool[] system, string rule, int? poisoned)
    {
        (BoundEffect[] effects, _) = Bound(new SyncSettlementFixture());

        EezSettlementException e = Assert.Throws<EezSettlementException>(() => OutboundCall.AuthorizeAll(effects, events, system, RollupId))!;

        Assert.That(e.Message, Does.Contain(rule));
        Assert.That(e.PoisonedTransactionIndex, Is.EqualTo(poisoned), "only a missing, malformed or gas-carrying event lets the composer evict the call");
    }

    [Test]
    public void Verify_EffectsTheSettlingBlockCannotHold_IsTheCallersFault()
    {
        SyncSettlementFixture fixture = new();
        Block empty = Build.A.Block.WithNumber(2).WithBeneficiary(Beneficiary).WithExtraData([9]).TestObject;

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            DaVerification.Verify(fixture.Payload(), [fixture.PrecedingBlock, empty], fixture.Outbound, fixture.Inbound, ChainId, RollupId))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));
    }

    [Test]
    public void Verify_AuthorizedEffectsThatCannotBeRebuilt_IsTheCallersFault()
    {
        SyncSettlementFixture fixture = new();
        AuthorizedOutbound[] outbound = [fixture.Outbound[0] with { DerivedDaEntry = fixture.OutboundDaEntry with { Success = false } }];
        byte[] payload = fixture.Payload(actions: [DaAction.FromEntry(outbound[0].DerivedDaEntry, RollupId), fixture.InboundAction]);

        Assert.That(Assert.Throws<EezSettlementException>(() => DaVerification.Verify(payload, fixture.Window, outbound, fixture.Inbound, ChainId, RollupId))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));
    }

    [Test]
    public void Verify_DaForAnotherRollup_IsInvalidDaPayload()
    {
        SyncSettlementFixture fixture = new();
        byte[] payload = DaPayloadCodec.Encode(RollupId + 1, [new DaBlock(Beneficiary, [], [])], []);

        Assert.That(Assert.Throws<EezSettlementException>(() => DaVerification.Verify(payload, fixture.Window, fixture.Outbound, fixture.Inbound, ChainId, RollupId))!.Failure,
            Is.EqualTo(EezSettlementFailure.InvalidDaPayload));
    }

    [Test]
    public void AuthorizeAll_OutboundAfterInbound_Throws()
    {
        SyncSettlementFixture fixture = new();
        (BoundEffect[] effects, SettlingBlock observed) = Bound(fixture);
        BoundEffect[] reordered = [effects[1] with { TransactionIndex = 0 }, effects[0]];

        Assert.Throws<EezSettlementException>(() => OutboundCall.AuthorizeAll(reordered, observed.OutboundEvents, observed.SystemTransactions, RollupId));
    }

    private static (BoundEffect[] Effects, SettlingBlock Observed) Bound(SyncSettlementFixture fixture)
    {
        SettlingBlock observed = SettlingBlock.Inspect(fixture.Settling, fixture.SettlingReceipts(), RollupId);
        (ExecutionEntry outbound, StateUpdate outboundUpdate, ExecutionEntry inbound, StateUpdate inboundUpdate) =
            fixture.ClaimedEntries(fixture.Settling.ParentHash!.ValueHash256, Word(3), Word(4));
        return ([new BoundEffect(1, 1, EntryShape.Outbound, outbound, outboundUpdate), new BoundEffect(2, 2, EntryShape.Inbound, inbound, inboundUpdate)], observed);
    }

    private static TestCaseData[] UnloweredEntries() =>
    [
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0], e.Calls[0]] }), true) { TestName = "OutboundMultiCall" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [] }), true) { TestName = "OutboundWithoutCall" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Success = false }), true) { TestName = "OutboundFailed" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0] with { IsStatic = true }] }), true) { TestName = "OutboundStatic" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0] with { Gas = 1 }] }), false) { TestName = "InboundWithGas" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { Calls = [e.Calls[0] with { RevertNextNCalls = 1 }] }), false) { TestName = "InboundRevertingNext" },
        new((Func<ExecutionEntry, ExecutionEntry>)(static e => e with { ExpectedCalls = [new ExpectedCall(default, [], default, true, [])] }), false) { TestName = "InboundExpectingCalls" },
    ];

    private static TestCaseData[] EventLogs()
    {
        byte[] dirtyPadding = EventData(0);
        dirtyPadding[^1] = 1;
        byte[] offCanonicalOffset = EventData(0);
        offCanonicalOffset[63] = 0xa0;
        byte[] dirtyProxy = new byte[32];
        dirtyProxy[0] = 1;
        LogEntry proxyWithHighBits = new(EezConstants.Eezl2Address, EventData(0), [OutboundEvent.Signature, Keccak.Zero, new Hash256(dirtyProxy)]);
        return
        [
            new(EventLog(Keccak.OfAnEmptyString.ValueHash256)) { ExpectedResult = true, TestName = "Canonical" },
            new(EventLog(default, topicCount: 2)) { ExpectedResult = false, TestName = "MissingProxyTopic" },
            new(EventLog(default, data: [.. EventData(0), 0])) { ExpectedResult = false, TestName = "TrailingData" },
            new(EventLog(default, data: dirtyPadding)) { ExpectedResult = false, TestName = "DirtyPadding" },
            new(EventLog(default, data: offCanonicalOffset)) { ExpectedResult = false, TestName = "NonCanonicalOffset" },
            new(proxyWithHighBits) { ExpectedResult = false, TestName = "ProxyWithHighBits" },
            new(new LogEntry(EezConstants.Eezl2Address, EventData(0), [.. EventLog(default).Topics, Keccak.Zero])) { ExpectedResult = false, TestName = "ExtraTopic" },
        ];
    }

    private static TestCaseData[] BadPayloads() =>
    [
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(actions: [f.Actions[0]])), "carries 1 actions") { TestName = "MissingAction" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(actions: [f.Actions[1], f.Actions[0]])), "does not rebuild") { TestName = "ActionsSwapped" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(actions: [f.Actions[0], f.InboundAction with { ReturnData = [0xcd] }])), "does not rebuild") { TestName = "ActionClaimsOtherResult" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(settlingPublished: [])), "publishes 0 transactions") { TestName = "UserTransactionOmitted" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(settlingPublished: f.SyncTransactions)), "publishes 3 transactions") { TestName = "SystemTransactionsPublished" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(settlingPublished: [f.PrecedingTransaction])), "does not publish transaction 1") { TestName = "OtherUserTransaction" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => f.Payload(settlingExtraData: [8])), "other extra data") { TestName = "OtherExtraData" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => DaPayloadCodec.Encode(RollupId + 1, [new DaBlock(Beneficiary, [], [])], [])), "carries rollup 2") { TestName = "OtherRollup" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => DaPayloadCodec.Encode(RollupId, [new DaBlock(Beneficiary, [9], [f.UserTransaction])], f.Actions)), "covers 1 blocks") { TestName = "MissingBlock" },
        new((Func<SyncSettlementFixture, byte[]>)(static f => DaPayloadCodec.Encode(RollupId, [new DaBlock(Address.Zero, [], [f.PrecedingTransaction]), new DaBlock(Beneficiary, [9], [f.UserTransaction])], f.Actions)),
            "another beneficiary") { TestName = "OtherBeneficiary" },
    ];

    private static TestCaseData[] BadEvents()
    {
        bool[] system = [true, false, true];
        OutboundEvent valid = new(1, 0, true, EventCallHash, 0);
        return
        [
            new(Array.Empty<OutboundEvent>(), system, "no event at transaction 1", 1) { TestName = "NoEvent" },
            new(new[] { valid, valid with { LogIndex = 1 } }, system, "claimed by no outbound entry", null) { TestName = "TwoEventsInOneTransaction" },
            new(new[] { valid with { TransactionIndex = 0 }, valid }, system, "no event at transaction 1", 1) { TestName = "EventBeforeTheEffect" },
            new(new[] { valid, valid with { TransactionIndex = 2 } }, system, "claimed by no outbound entry", null) { TestName = "EventAtTheDelivery" },
            new(new[] { valid with { TransactionIndex = 2 } }, system, "no event at transaction 1", 1) { TestName = "OnlyEventIsAtTheDelivery" },
            new(new[] { valid with { IsCanonical = false } }, system, "malformed", 1) { TestName = "MalformedEvent" },
            new(new[] { valid with { CallHash = Keccak.Zero.ValueHash256 } }, system, "different call", null) { TestName = "OtherCall" },
            new(new[] { valid with { CallGas = 1 } }, system, "call with gas", 1) { TestName = "EventWithGas" },
            new(new[] { valid }, new[] { false, false, true }, "no system load", null) { TestName = "NoLoadBeforeTheUser" },
        ];
    }

    private static bool SameCandidate(InboundCandidate x, InboundCandidate y) =>
        x.TransactionIndex == y.TransactionIndex && x.Error == y.Error && (x.Observation is null) == (y.Observation is null)
        && (x.Observation is null || x.Observation.CallHash == y.Observation!.CallHash);

}
