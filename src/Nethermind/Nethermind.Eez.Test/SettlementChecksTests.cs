// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.Eez.Test.SyncSettlementFixture;
using static Nethermind.Eez.Test.TestWords;

namespace Nethermind.Eez.Test;

public class SettlementChecksTests
{
    private const ulong RollupId = 1;
    private static readonly Address ProofSystem = new("0xd0e17aefa018030e7a9652da302d4317f30f95b0");

    [Test]
    public void RecordedBatch_PassesEveryCheckAsOneAnchorWithoutEffects()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(StatelessFixtures.Window84, "oracle.json");
        PostBatch batch = RecordedBatch();
        ValueHash256 pre = new(oracle.GetProperty("window_pre_block_hash").GetString()!);
        ValueHash256 post = new(oracle.GetProperty("window_post_block_hash").GetString()!);

        PostBatchProfile.Validate(batch, RollupId, new Address(oracle.GetProperty("proof_system").GetString()!));
        RollupUpdate[] updates = RollupUpdateChain.Verify(batch, RollupId, pre, post);
        BoundEffect[] effects = EffectBinding.Bind(batch, updates, RollupId, [], [], []);

        Assert.That(EntryShapes.Classify(batch.Entries[0], updates[0], RollupId), Is.EqualTo(EntryShape.Anchor), "the recorded entry is the canonical anchor");
        Assert.That(effects, Is.Empty, "a window without cross-chain calls binds no effects");
        Assert.That(PostBatchProfile.PublicInputsHash(batch, RollupId, new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!)),
            Is.EqualTo(new ValueHash256(oracle.GetProperty("public_inputs_hash").GetString()!)), "the profile's public inputs hash is the one the signer signed");
    }

    [TestCaseSource(nameof(OutOfProfileBatches))]
    public void Validate_BatchOutsideTheProfile_Throws(Func<PostBatch, PostBatch> mutate) =>
        Assert.Throws<EezSettlementException>(() => PostBatchProfile.Validate(mutate(RecordedBatch()), RollupId, ProofSystem));

    [Test]
    public void Validate_ImmediateCountIsTheLeadingRunWithoutProxy()
    {
        PostBatch batch = RecordedBatch();
        ExecutionEntry inbound = batch.Entries[0] with { ProxyEntryHash = Word(9) };
        PostBatch twoEntries = batch with { Entries = [batch.Entries[0], inbound, batch.Entries[0]], ImmediateEntryCount = 1 };

        Assert.DoesNotThrow(() => PostBatchProfile.Validate(twoEntries, RollupId, ProofSystem), "the run stops at the first entry with a proxy entry hash");
        Assert.Throws<EezSettlementException>(() => PostBatchProfile.Validate(twoEntries with { ImmediateEntryCount = 2 }, RollupId, ProofSystem),
            "an entry after a proxied entry is not immediate");
    }

    [TestCaseSource(nameof(BrokenChains))]
    public void Verify_UpdatesThatDoNotChainTheWindow_Throw(RollupUpdate[][] updates, string rule)
    {
        ExecutionEntry template = RecordedBatch().Entries[0];
        PostBatch batch = RecordedBatch() with { Entries = Array.ConvertAll(updates, u => template with { RollupUpdates = u }) };

        Assert.That(Assert.Throws<EezSettlementException>(() => RollupUpdateChain.Verify(batch, RollupId, Word(1), Word(3)))!.Message, Does.Contain(rule));
    }

    [Test]
    public void Verify_ChainedUpdates_ReturnsThemInOrder()
    {
        ExecutionEntry template = RecordedBatch().Entries[0];
        RollupUpdate first = Update(1, 2);
        RollupUpdate second = Update(2, 3);
        PostBatch batch = RecordedBatch() with { Entries = [template with { RollupUpdates = [first] }, template with { RollupUpdates = [second] }] };

        Assert.That(RollupUpdateChain.Verify(batch, RollupId, Word(1), Word(3)), Is.EqualTo(new[] { first, second }));
    }

    [TestCaseSource(nameof(Shapes))]
    public EntryShape Classify_Entry_ReturnsItsShape(ExecutionEntry entry) => EntryShapes.Classify(entry, Update(1, 2), RollupId);

    [Test]
    public void InspectInbound_Delivery_ObservesTheCallAndDerivesItsDaEntry()
    {
        (IncomingCrossChainCall call, ValueHash256 callHash) = Delivery(7, [0xab]);

        InboundObservation observation = InboundDelivery.Inspect(7, EezCalldata.EncodeExecuteIncomingCrossChainCall(call), true, RollupId);

        Assert.That(observation.CallHash, Is.EqualTo(callHash), "the call hash is recomputed from the delivered call");
        Assert.That(observation.ReturnData, Is.EqualTo(new byte[] { 0xab }));
        ExecutionEntry expected = new([], callHash, call.Entries[0].IncomingCalls, [], call.Entries[0].RollingHash, RollupId, true, [0xab]);
        Assert.That(EezCalldata.EntryHash(observation.DerivedDaEntry), Is.EqualTo(EezCalldata.EntryHash(expected)),
            "the DA entry carries the delivered call and the L2 rolling hash, and no state updates");
    }

    [TestCaseSource(nameof(BadDeliveries))]
    public void InspectInbound_BadDelivery_Throws(Func<IncomingCrossChainCall, IncomingCrossChainCall> mutate, UInt256 transactionValue, bool succeeded, string rule) =>
        Assert.That(Assert.Throws<EezSettlementException>(() =>
            InboundDelivery.Inspect(transactionValue, EezCalldata.EncodeExecuteIncomingCrossChainCall(mutate(Delivery(7, [0xab]).Call)), succeeded, RollupId))!.Message,
            Does.Contain(rule));

    [Test]
    public void InspectInbound_UndecodableCalldata_Throws() =>
        Assert.Throws<EezSettlementException>(() => InboundDelivery.Inspect(0, [1, 2, 3], true, RollupId));

    [TestCaseSource(nameof(InboundClaims))]
    public void Authorize_InboundClaim_AcceptsOnlyTheObservedDelivery(Func<ExecutionEntry, RollupUpdate, (ExecutionEntry, RollupUpdate)> mutate, string? rule)
    {
        (IncomingCrossChainCall call, _) = Delivery(7, [0xab]);
        InboundObservation observation = InboundDelivery.Inspect(7, EezCalldata.EncodeExecuteIncomingCrossChainCall(call), true, RollupId);
        (ExecutionEntry entry, RollupUpdate update) = mutate(InboundEntry(observation, out RollupUpdate valid), valid);

        AssertRule(() => InboundDelivery.Authorize(entry, update, observation, RollupId), rule);
    }

    [TestCaseSource(nameof(OutboundClaims))]
    public void Authorize_OutboundClaim_AcceptsOnlyTheObservedCall(Func<ExecutionEntry, RollupUpdate, (ExecutionEntry, RollupUpdate)> mutate, ulong eventCallGas, string? rule)
    {
        ExecutionEntry valid = OutboundEntry(L2Contract, out RollupUpdate validUpdate, out _);
        (ExecutionEntry entry, RollupUpdate update) = mutate(valid, validUpdate);
        CrossChainCall call = valid.Calls[0];
        ValueHash256 eventHash = CrossChainCallHash.Compute(false, call.SourceAddress, RollupId, call.TargetAddress, 0, call.Value, eventCallGas, call.Data);

        AssertRule(() => OutboundCall.Authorize(entry, update, eventHash, eventCallGas, RollupId), rule);
    }

    [Test]
    public void AuthorizeOutbound_ValidCall_LeavesTheResultToDa()
    {
        ExecutionEntry entry = OutboundEntry(L2Contract, out RollupUpdate update, out ValueHash256 callHash);

        (ExecutionEntry baseDaEntry, ValueHash256 pending) = OutboundCall.Authorize(entry, update, callHash, 0, RollupId);

        Assert.That(baseDaEntry, Is.EqualTo(entry with { RollupUpdates = [], RollingHash = default }), "the DA entry drops the updates and the L1 rolling hash");
        Assert.That(RollingHash.CallEnd(pending, true, PublishedResult), Is.EqualTo(entry.RollingHash), "the call's end, closed with DA's result, is the claimed rolling hash");
    }

    [Test]
    public void AuthorizeOutbound_SystemAddressAsSource_Throws()
    {
        ExecutionEntry entry = OutboundEntry(EezConstants.SystemAddress, out RollupUpdate update, out ValueHash256 callHash);

        Assert.That(Assert.Throws<EezSettlementException>(() => OutboundCall.Authorize(entry, update, callHash, 0, RollupId))!.Message, Does.Contain("system address"));
    }

    [Test]
    public void Credit_ValueBeyondInt192_Throws() =>
        Assert.Throws<EezSettlementException>(() => EtherDelta.Credit(UInt256.One << 191));

    [Test]
    public void Debit_LargestInt192_IsItsNegation() =>
        Assert.That(EtherDelta.Debit((UInt256.One << 191) - 1), Is.EqualTo(new Int256.Int256(UInt256.MaxValue - ((UInt256.One << 191) - 1) + 1)));

    [Test]
    public void Bind_AnchorThenEffects_BindsEachEntryToItsTransaction()
    {
        (PostBatch batch, RollupUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = EffectBatch();

        BoundEffect[] effects = EffectBinding.Bind(batch, updates, RollupId, checkpoints, [1, 3], [true, false, true, true]);

        Assert.That(effects, Is.EqualTo(new[]
        {
            new BoundEffect(1, 1, EntryShape.Outbound, batch.Entries[1], updates[1]),
            new BoundEffect(2, 3, EntryShape.Inbound, batch.Entries[2], updates[2]),
        }));
    }

    [TestCaseSource(nameof(BadBindings))]
    public void Bind_ClaimsThatDoNotMatchTheSettlingBlock_Throw(Func<(PostBatch, RollupUpdate[], EezTransactionCheckpoint[]), (PostBatch, RollupUpdate[], EezTransactionCheckpoint[])> mutate,
        int[] effectTransactions, bool[] systemTransactions, string rule)
    {
        (PostBatch batch, RollupUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = mutate(EffectBatch());

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EffectBinding.Bind(batch, updates, RollupId, checkpoints, effectTransactions, systemTransactions))!.Message, Does.Contain(rule));
    }

    [Test]
    public void Bind_CheckpointsThatDoNotMatchTheEffects_IsTheCallersFault()
    {
        (PostBatch batch, RollupUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = EffectBatch();

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EffectBinding.Bind(batch, updates, RollupId, [checkpoints[0], checkpoints[1], checkpoints[2] with { TransactionIndex = 2 }], [1, 3], [true, false, true, true]))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));
    }

    [Test]
    public void Bind_FewerCheckpointsThanEffects_IsTheCallersFault()
    {
        (PostBatch batch, RollupUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = EffectBatch();

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EffectBinding.Bind(batch, updates, RollupId, checkpoints[..2], [1, 3], [true, false, true, true]))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));
    }

    [Test]
    public void Bind_NoEffectsButCheckpoints_Throws()
    {
        PostBatch batch = RecordedBatch();
        RollupUpdate[] updates = [batch.Entries[0].RollupUpdates[0]];

        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EffectBinding.Bind(batch, updates, RollupId, [new EezTransactionCheckpoint(EezTransactionCheckpoint.PreExecution, Keccak.Zero, Keccak.Zero)], [], []))!.Message,
            Does.Contain("without transactions"), "a batch without effects claims the whole settling block, so it may hold none");
    }

    private static TestCaseData[] OutOfProfileBatches() =>
    [
        Case(static b => b with { ProofSystems = [] }, "NoProofSystem"),
        Case(static b => b with { ProofSystems = [L1Sender] }, "OtherProofSystem"),
        Case(static b => b with { ProofSystems = [ProofSystem, ProofSystem] }, "TwoProofSystems"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(2, [0])] }, "OtherRollup"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(RollupId, [1])] }, "OtherProofSystemIndex"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(RollupId, [0]), new RollupProofSystems(RollupId, [0])] }, "TwoRollups"),
        Case(static b => b with { ExpectedRoots = [new ExpectedRoot(RollupId, Word(1))] }, "ExpectedRoots"),
        Case(static b => b with { Entries = [b.Entries[0] with { DestinationRollupId = 2 }] }, "EntryForOtherRollup"),
        Case(static b => b with { StaticEntries = [new StaticExecutionEntry([], Word(1), [], Word(2), RollupId, true, [])] }, "StaticEntries"),
        Case(static b => b with { ImmediateStaticEntryCount = 1 }, "ImmediateStaticEntries"),
        Case(static b => b with { ImmediateEntryCount = 0 }, "ImmediateCountBelowLeadingRun"),
        Case(static b => b with { BlockNumber = 1 }, "BlockNumber"),
        Case(static b => b with { BlobIndices = [0] }, "BlobIndices"),
        Case(static b => b with { BindMsgSenderInPublicInput = true }, "BindsSender"),
    ];

    private static TestCaseData[] BrokenChains() =>
    [
        new(new[] { new[] { Update(1, 2), Update(2, 3) } }, "exactly one state update") { TestName = "TwoUpdatesInOneEntry" },
        new(new[] { new[] { Update(1, 3), Update(1, 3) with { RollupId = 2 } } }, "exactly one state update") { TestName = "ExtraUpdateAfterTheChain" },
        new(new[] { Array.Empty<RollupUpdate>() }, "exactly one state update") { TestName = "NoUpdate" },
        new(Array.Empty<RollupUpdate[]>(), "no execution entries") { TestName = "NoEntries" },
        new(new[] { new[] { Update(9, 3) } }, "starts from") { TestName = "WrongStart" },
        new(new[] { new[] { Update(1, 2) } }, "not at the window's last block") { TestName = "WrongEnd" },
        new(new[] { new[] { Update(1, 2) }, new[] { Update(9, 3) } }, "starts from") { TestName = "BrokenLink" },
        new(new[] { new[] { Update(1, 3) with { RollupId = 2 } } }, "updates rollup 2") { TestName = "OtherRollup" },
    ];

    private static TestCaseData[] Shapes()
    {
        ExecutionEntry anchor = new([Update(1, 2)], default, [], [], RollingHash.SeedL1([new StateCommitment(RollupId, Word(1))], default), RollupId, true, []);
        CrossChainCall flat = new(0, false, 0, L2Contract, RollupId, L1Sender, 0, []);
        return
        [
            new TestCaseData(anchor) { ExpectedResult = EntryShape.Anchor, TestName = "Anchor" },
            new TestCaseData(anchor with { RollingHash = Word(5) }) { ExpectedResult = EntryShape.Invalid, TestName = "AnchorWithWrongRollingHash" },
            new TestCaseData(anchor with { ReturnData = [1] }) { ExpectedResult = EntryShape.Invalid, TestName = "AnchorWithReturnData" },
            new TestCaseData(anchor with { DestinationRollupId = 2 }) { ExpectedResult = EntryShape.Invalid, TestName = "AnchorForOtherRollup" },
            new TestCaseData(anchor with { Success = false }) { ExpectedResult = EntryShape.Invalid, TestName = "Failed" },
            new TestCaseData(anchor with { ExpectedCalls = [new ExpectedCall(Word(1), [], Word(2), true, [])] }) { ExpectedResult = EntryShape.Invalid, TestName = "ExpectsCalls" },
            new TestCaseData(anchor with { ProxyEntryHash = Word(7) }) { ExpectedResult = EntryShape.Inbound, TestName = "Inbound" },
            new TestCaseData(anchor with { ProxyEntryHash = Word(7), Calls = [flat] }) { ExpectedResult = EntryShape.Invalid, TestName = "InboundWithCalls" },
            new TestCaseData(anchor with { Calls = [flat] }) { ExpectedResult = EntryShape.Outbound, TestName = "Outbound" },
            new TestCaseData(anchor with { Calls = [flat, flat] }) { ExpectedResult = EntryShape.Invalid, TestName = "OutboundWithTwoCalls" },
            new TestCaseData(anchor with { Calls = [flat with { RevertNextNCalls = 1 }] }) { ExpectedResult = EntryShape.Invalid, TestName = "OutboundRevertingNext" },
            new TestCaseData(anchor with { Calls = [flat with { IsStatic = true }] }) { ExpectedResult = EntryShape.Invalid, TestName = "OutboundStatic" },
            new TestCaseData(anchor with { Calls = [flat with { Gas = 1 }] }) { ExpectedResult = EntryShape.Invalid, TestName = "OutboundWithGas" },
        ];
    }

    private static TestCaseData[] BadDeliveries() =>
    [
        new((Func<IncomingCrossChainCall, IncomingCrossChainCall>)(static c => c), (UInt256)7, false, "reverted") { TestName = "Reverted" },
        new((Func<IncomingCrossChainCall, IncomingCrossChainCall>)(static c => c), (UInt256)8, true, "native value") { TestName = "NativeValueDiffers" },
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { SourceRollupId = 2 }), "does not come from L1", "SourceNotL1"),
        BadDelivery(static c => c with { Entries = [] }, "exactly one entry", "NoEntry"),
        BadDelivery(static c => c with { Entries = [c.Entries[0], c.Entries[0]] }, "exactly one entry", "TwoEntries"),
        BadDelivery(static c => c with { StaticEntries = [new L2StaticExecutionEntry(0, Word(1), [], Word(2), true, [])] }, "no static entries", "StaticEntry"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { IncomingCalls = [] }] }, "exactly one incoming call", "NoIncomingCall"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { IncomingCalls = [c.Entries[0].IncomingCalls[0], c.Entries[0].IncomingCalls[0]] }] },
            "exactly one incoming call", "TwoIncomingCalls"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { Success = false }] }, "must succeed", "EntryFailed"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { ExpectedOutgoingCalls = [new ExpectedCall(Word(1), [], Word(2), true, [])] }] },
            "no outgoing calls", "ExpectsOutgoingCalls"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { TargetAddress = L1Sender }), "proxy entry hash", "DestinationNotHashed"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { Value = 8 }), "native value", "ValueNotSent"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { Data = [9] }), "proxy entry hash", "DataNotHashed"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { SourceAddress = L2Contract }), "proxy entry hash", "SourceAddressNotHashed"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { Gas = 1 }), "flat, mutable", "InnerWithGas"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { IsStatic = true }), "flat, mutable", "InnerStatic"),
        BadDelivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { RevertNextNCalls = 1 }), "flat, mutable", "InnerRevertingNext"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { ProxyEntryHash = Word(1) }] }, "proxy entry hash", "ProxyIsNotTheCallHash"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { RollingHash = Word(1) }] }, "rolling hash", "RollingHashDiffers"),
        BadDelivery(static c => c with { Entries = [c.Entries[0] with { ReturnData = [0xcd] }] }, "rolling hash", "ReturnDataNotInRollingHash"),
    ];

    private static TestCaseData[] InboundClaims() =>
    [
        Claim(static (e, u) => (e, u), null, "Valid"),
        Claim(static (e, u) => (e with { DestinationRollupId = 2 }, u), "must target rollup", "OtherRollup"),
        Claim(static (e, u) => (e with { ProxyEntryHash = Word(1), RollingHash = RollingHash.SeedL1(u, Word(1)) }, u), "different call", "OtherCall"),
        Claim(static (e, u) => (e with { ReturnData = [0xcd] }, u), "different result", "OtherResult"),
        Claim(static (e, u) => (e with { RollingHash = Word(1) }, u), "L1 seed", "RollingHashNotTheL1Seed"),
        Claim(static (e, u) => (e, u with { EtherDelta = Int256.Int256.Zero }), "credited", "NotCredited"),
        Claim(static (e, u) => (e, u with { EtherDelta = new Int256.Int256(-7) }), "credited", "Debited"),
    ];

    private static TestCaseData[] OutboundClaims() =>
    [
        OutboundClaim(static (e, u) => (e, u), 0, null, "Valid"),
        OutboundClaim(static (e, u) => (e, u), 1, "without gas", "EventWithGas"),
        OutboundClaim(static (e, u) => (e with { DestinationRollupId = 2 }, u), 0, "must target rollup", "OtherRollup"),
        OutboundClaim(static (e, u) => (e with { Calls = [e.Calls[0] with { SourceRollupId = 2 }] }, u), 0, "must come from rollup", "CallFromOtherRollup"),
        OutboundClaim(static (e, u) => (e with { Calls = [e.Calls[0] with { Data = [9] }] }, u), 0, "different call", "OtherCall"),
        OutboundClaim(static (e, u) => (e with { Calls = [] }, u), 0, "exactly one call", "NoCall"),
        OutboundClaim(static (e, u) => (e with { ReturnData = [0xcd] }, u), 0, "no return data", "ResultOnL1"),
        OutboundClaim(static (e, u) => (e with { Success = false }, u), 0, "must succeed", "Failed"),
        OutboundClaim(static (e, u) => (e, u with { EtherDelta = new Int256.Int256(3) }), 0, "debited", "CreditedInsteadOfDebited"),
        OutboundClaim(static (e, u) => (e, u with { EtherDelta = Int256.Int256.Zero }), 0, "debited", "NotDebited"),
    ];

    private static TestCaseData[] BadBindings() =>
    [
        Binding(static s => s, [1], [true, false, true, true], "claims 2 effects", "FewerEffectTransactions"),
        Binding(static s => s, [1, 3, 4], [true, false, true, true, false], "claims 2 effects", "MoreEffectTransactions"),
        Binding(static s => s, [1, 3], [true, true, true, true], "is Outbound, but transaction 1 is Inbound", "OutboundEntryAtSystemTransaction"),
        Binding(static s => s, [1, 3], [true, false, true, false], "is Inbound, but transaction 3 is Outbound", "InboundEntryAtUserTransaction"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0] with { BlockHash = new Hash256(Word(9)) }, s.Item3[1], s.Item3[2]]), [1, 3], [true, false, true, true],
            "settling block's empty prefix", "AnchorDoesNotEndAtTheEmptyPrefix"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0] with { TransactionIndex = 0 }, s.Item3[1], s.Item3[2]]), [1, 3], [true, false, true, true],
            "not at the empty prefix", "AnchorCheckpointAfterATransaction"),
        Binding(static s => (s.Item1, s.Item2, s.Item3[..2]), [1, 3], [true, false, true, true], "needs 3 transaction checkpoints", "MissingCheckpoint"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0], s.Item3[1], s.Item3[2] with { TransactionIndex = 2 }]), [1, 3], [true, false, true, true],
            "is at transaction 2", "CheckpointAtOtherTransaction"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0], s.Item3[1], s.Item3[2] with { BlockHash = Keccak.Zero }]), [1, 3], [true, false, true, true],
            "claims block", "ClaimsOtherCandidateBlock"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[1], s.Item1.Entries[1], s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3], [true, false, true, true],
            "leading entry", "LeadingEntryNotAnchor"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[0], Anchor(s.Item2[1]), s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3], [true, false, true, true],
            "second anchor", "SecondAnchor"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[0], s.Item1.Entries[1] with { Success = false }, s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3],
            [true, false, true, true], "not a valid effect", "InvalidEffect"),
        Binding(static s => (s.Item1, [s.Item2[0] with { EtherDelta = Int256.Int256.One }, s.Item2[1], s.Item2[2]], s.Item3), [1, 3], [true, false, true, true],
            "moves", "AnchorMovesEther"),
    ];

    private static (PostBatch Batch, RollupUpdate[] Updates, EezTransactionCheckpoint[] Checkpoints) EffectBatch()
    {
        RollupUpdate[] updates = [Update(1, 2), Update(2, 3), Update(3, 4)];
        ExecutionEntry anchor = new([updates[0]], default, [], [], RollingHash.SeedL1([new StateCommitment(RollupId, Word(1))], default), RollupId, true, []);
        ExecutionEntry outbound = new([updates[1]], default, [new CrossChainCall(0, false, 0, L2Contract, RollupId, L1Sender, 0, [])], [], Word(5), RollupId, true, []);
        ExecutionEntry inbound = new([updates[2]], Word(6), [], [], Word(7), RollupId, true, []);
        PostBatch batch = RecordedBatch() with { Entries = [anchor, outbound, inbound] };
        return (batch, updates,
        [
            new EezTransactionCheckpoint(EezTransactionCheckpoint.PreExecution, Keccak.Zero, new Hash256(Word(2))),
            new EezTransactionCheckpoint(1, Keccak.Zero, new Hash256(Word(3))),
            new EezTransactionCheckpoint(3, Keccak.Zero, new Hash256(Word(4))),
        ]);
    }

    private static (IncomingCrossChainCall Call, ValueHash256 CallHash) Delivery(UInt256 value, byte[] returnData)
    {
        byte[] data = [1, 2, 3];
        CrossChainCall inner = new(0, false, 0, L1Sender, 0, L2Contract, value, data);
        ValueHash256 callHash = CrossChainCallHash.Compute(false, L1Sender, 0, L2Contract, RollupId, value, 0, data);
        ValueHash256 rolling = RollingHash.SingleL2Call(callHash, true, returnData);
        L2ExecutionEntry entry = new(callHash, [inner], [], rolling, true, returnData);
        return (new IncomingCrossChainCall([entry], []), callHash);
    }

    private static ExecutionEntry InboundEntry(InboundObservation observation, out RollupUpdate update)
    {
        update = Update(1, 2) with { EtherDelta = new Int256.Int256(7) };
        ValueHash256 rolling = RollingHash.SeedL1(update, observation.CallHash);
        return new ExecutionEntry([update], observation.CallHash, [], [], rolling, RollupId, true, observation.ReturnData);
    }

    /// <summary>The outbound call's result, which only DA publishes; the L1 entry's rolling hash records it.</summary>
    private static readonly byte[] PublishedResult = [0xee];

    private static ExecutionEntry OutboundEntry(Address source, out RollupUpdate update, out ValueHash256 callHash)
    {
        CrossChainCall call = new(0, false, 0, source, RollupId, L1Sender, 3, [4, 5]);
        update = Update(1, 2) with { EtherDelta = new Int256.Int256(-3) };
        callHash = CrossChainCallHash.Compute(false, source, RollupId, L1Sender, 0, 3, 0, call.Data);
        ValueHash256 rolling = RollingHash.CallEnd(
            RollingHash.CallBegin(RollingHash.SeedL1(update, default), callHash), true, PublishedResult);
        return new ExecutionEntry([update], default, [call], [], rolling, RollupId, true, []);
    }

    private static IncomingCrossChainCall WithInner(IncomingCrossChainCall call, CrossChainCall inner) =>
        call with { Entries = [call.Entries[0] with { IncomingCalls = [inner] }] };

    private static TestCaseData Case(Func<PostBatch, PostBatch> mutate, string name) => new(mutate) { TestName = name };

    private static TestCaseData BadDelivery(Func<IncomingCrossChainCall, IncomingCrossChainCall> mutate, string rule, string name) =>
        new(mutate, (UInt256)7, true, rule) { TestName = name };

    private static TestCaseData Claim(Func<ExecutionEntry, RollupUpdate, (ExecutionEntry, RollupUpdate)> mutate, string? rule, string name) =>
        new(mutate, rule) { TestName = name };

    private static TestCaseData OutboundClaim(Func<ExecutionEntry, RollupUpdate, (ExecutionEntry, RollupUpdate)> mutate, ulong eventCallGas, string? rule, string name) =>
        new(mutate, eventCallGas, rule) { TestName = name };

    private static void AssertRule(Action check, string? rule)
    {
        if (rule is null)
        {
            Assert.DoesNotThrow(check);
        }
        else
        {
            Assert.That(Assert.Throws<EezSettlementException>(check)!.Message, Does.Contain(rule));
        }
    }

    private static TestCaseData Binding(
        Func<(PostBatch, RollupUpdate[], EezTransactionCheckpoint[]), (PostBatch, RollupUpdate[], EezTransactionCheckpoint[])> mutate,
        int[] effectTransactions, bool[] systemTransactions, string rule, string name) =>
        new(mutate, effectTransactions, systemTransactions, rule) { TestName = name };

    private static ExecutionEntry Anchor(RollupUpdate update) => new([update], default, [], [], RollingHash.SeedL1(update, default), RollupId, true, []);

    private static RollupUpdate Update(ulong from, ulong to) => new(RollupId, Word(from), Word(to), Int256.Int256.Zero);

    private static PostBatch RecordedBatch() => EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(StatelessFixtures.Window84));

}
