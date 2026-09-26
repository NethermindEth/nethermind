// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SettlementChecksTests
{
    private const string Window84 = "captured-devnet-window-84";
    private const ulong RollupId = 1;
    private static readonly Address L1Sender = new("0x00000000000000000000000000000000000000aa");
    private static readonly Address L2Contract = new("0x00000000000000000000000000000000000000bb");
    private static readonly Address ProofSystem = new("0xd0e17aefa018030e7a9652da302d4317f30f95b0");

    [Test]
    public void RecordedBatch_PassesEveryCheckAsOneAnchorWithoutEffects()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Window84, "oracle.json");
        PostBatch batch = RecordedBatch();
        ValueHash256 pre = new(oracle.GetProperty("window_pre_block_hash").GetString()!);
        ValueHash256 post = new(oracle.GetProperty("window_post_block_hash").GetString()!);

        PostBatchProfile.Validate(batch, RollupId, new Address(oracle.GetProperty("proof_system").GetString()!));
        StateUpdate[] updates = StateUpdateChain.Verify(batch, RollupId, pre, post);
        BoundEffect[] effects = EffectBinding.Bind(batch, updates, RollupId, post, [], [], []);

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
    public void Verify_UpdatesThatDoNotChainTheWindow_Throw(StateUpdate[][] updates)
    {
        ExecutionEntry template = RecordedBatch().Entries[0];
        PostBatch batch = RecordedBatch() with { Entries = Array.ConvertAll(updates, u => template with { StateUpdates = u }) };

        Assert.Throws<EezSettlementException>(() => StateUpdateChain.Verify(batch, RollupId, Word(1), Word(3)));
    }

    [Test]
    public void Verify_ChainedUpdates_ReturnsThemInOrder()
    {
        ExecutionEntry template = RecordedBatch().Entries[0];
        StateUpdate first = Update(1, 2);
        StateUpdate second = Update(2, 3);
        PostBatch batch = RecordedBatch() with { Entries = [template with { StateUpdates = [first] }, template with { StateUpdates = [second] }] };

        Assert.That(StateUpdateChain.Verify(batch, RollupId, Word(1), Word(3)), Is.EqualTo(new[] { first, second }));
    }

    [TestCaseSource(nameof(Shapes))]
    public EntryShape Classify(ExecutionEntry entry) => EntryShapes.Classify(entry, Update(1, 2), RollupId);

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
    public void InspectInbound_BadDelivery_Throws(Func<IncomingCrossChainCall, IncomingCrossChainCall> mutate, UInt256 transactionValue, bool succeeded) =>
        Assert.Throws<EezSettlementException>(() =>
            InboundDelivery.Inspect(transactionValue, EezCalldata.EncodeExecuteIncomingCrossChainCall(mutate(Delivery(7, [0xab]).Call)), succeeded, RollupId));

    [Test]
    public void InspectInbound_UndecodableCalldata_Throws() =>
        Assert.Throws<EezSettlementException>(() => InboundDelivery.Inspect(0, [1, 2, 3], true, RollupId));

    [TestCaseSource(nameof(InboundClaims))]
    public bool AuthorizeInbound(Func<ExecutionEntry, StateUpdate, (ExecutionEntry, StateUpdate)> mutate)
    {
        (IncomingCrossChainCall call, _) = Delivery(7, [0xab]);
        InboundObservation observation = InboundDelivery.Inspect(7, EezCalldata.EncodeExecuteIncomingCrossChainCall(call), true, RollupId);
        (ExecutionEntry entry, StateUpdate update) = mutate(InboundEntry(observation, out StateUpdate valid), valid);
        try
        {
            InboundDelivery.Authorize(entry, update, observation, RollupId);
            return true;
        }
        catch (EezSettlementException)
        {
            return false;
        }
    }

    [TestCaseSource(nameof(OutboundClaims))]
    public bool AuthorizeOutbound(Func<ExecutionEntry, StateUpdate, (ExecutionEntry, StateUpdate)> mutate, ulong eventCallGas)
    {
        (ExecutionEntry entry, StateUpdate update) = mutate(OutboundEntry(L2Contract, out StateUpdate valid, out _), valid);
        CrossChainCall call = OutboundEntry(L2Contract, out _, out _).Calls[0];
        ValueHash256 eventHash = CrossChainCallHash.Compute(false, call.SourceAddress, RollupId, call.TargetAddress, 0, call.Value, 0, call.Data);
        try
        {
            OutboundCall.Authorize(entry, update, eventHash, eventCallGas, RollupId);
            return true;
        }
        catch (EezSettlementException)
        {
            return false;
        }
    }

    [Test]
    public void AuthorizeOutbound_DerivedDaEntry_DropsStateUpdatesAndTheL1RollingHash()
    {
        ExecutionEntry entry = OutboundEntry(L2Contract, out StateUpdate update, out ValueHash256 callHash);

        ExecutionEntry derived = OutboundCall.Authorize(entry, update, callHash, 0, RollupId);

        Assert.That(derived, Is.EqualTo(entry with { StateUpdates = [], RollingHash = default }));
    }

    [Test]
    public void AuthorizeOutbound_SystemAddressAsSource_Throws()
    {
        ExecutionEntry entry = OutboundEntry(EezConstants.SystemAddress, out StateUpdate update, out ValueHash256 callHash);

        Assert.Throws<EezSettlementException>(() => OutboundCall.Authorize(entry, update, callHash, 0, RollupId));
    }

    [Test]
    public void Bind_AnchorThenEffects_BindsEachEntryToItsTransaction()
    {
        (PostBatch batch, StateUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = EffectBatch();

        BoundEffect[] effects = EffectBinding.Bind(batch, updates, RollupId, Word(2), checkpoints, [1, 3], [true, false, true, true]);

        Assert.That(effects, Is.EqualTo(new[]
        {
            new BoundEffect(1, 1, EntryShape.Outbound, batch.Entries[1], updates[1]),
            new BoundEffect(2, 3, EntryShape.Inbound, batch.Entries[2], updates[2]),
        }));
    }

    [TestCaseSource(nameof(BadBindings))]
    public void Bind_ClaimsThatDoNotMatchTheSettlingBlock_Throw(Func<(PostBatch, StateUpdate[], EezTransactionCheckpoint[]), (PostBatch, StateUpdate[], EezTransactionCheckpoint[])> mutate,
        int[] effectTransactions, bool[] systemTransactions, ulong preSettling)
    {
        (PostBatch batch, StateUpdate[] updates, EezTransactionCheckpoint[] checkpoints) = mutate(EffectBatch());

        Assert.Throws<EezSettlementException>(() => EffectBinding.Bind(batch, updates, RollupId, Word(preSettling), checkpoints, effectTransactions, systemTransactions));
    }

    [Test]
    public void Bind_NoEffectsButCheckpoints_Throws()
    {
        PostBatch batch = RecordedBatch();
        StateUpdate[] updates = [batch.Entries[0].StateUpdates[0]];

        Assert.Throws<EezSettlementException>(() => EffectBinding.Bind(batch, updates, RollupId, default, [new EezTransactionCheckpoint(0, Keccak.Zero, Keccak.Zero)], [], []));
    }

    private static TestCaseData[] OutOfProfileBatches() =>
    [
        Case(static b => b with { ProofSystems = [] }, "NoProofSystem"),
        Case(static b => b with { ProofSystems = [L1Sender] }, "OtherProofSystem"),
        Case(static b => b with { ProofSystems = [ProofSystem, ProofSystem] }, "TwoProofSystems"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(2, [0])] }, "OtherRollup"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(RollupId, [1])] }, "OtherProofSystemIndex"),
        Case(static b => b with { RollupIdsWithProofSystems = [new RollupProofSystems(RollupId, [0]), new RollupProofSystems(RollupId, [0])] }, "TwoRollups"),
        Case(static b => b with { ExpectedStateRoots = [new ExpectedStateRoot(RollupId, Word(1))] }, "ExpectedStateRoots"),
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
        new((object)new[] { new[] { Update(1, 2), Update(2, 3) } }) { TestName = "TwoUpdatesInOneEntry" },
        new((object)new[] { Array.Empty<StateUpdate>() }) { TestName = "NoUpdate" },
        new((object)new[] { new[] { Update(9, 3) } }) { TestName = "WrongStart" },
        new((object)new[] { new[] { Update(1, 2) } }) { TestName = "WrongEnd" },
        new((object)new[] { new[] { Update(1, 2) }, new[] { Update(9, 3) } }) { TestName = "BrokenLink" },
        new((object)new[] { new[] { Update(1, 3) with { RollupId = 2 } } }) { TestName = "OtherRollup" },
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
        new((Func<IncomingCrossChainCall, IncomingCrossChainCall>)(static c => c), (UInt256)7, false) { TestName = "Reverted" },
        new((Func<IncomingCrossChainCall, IncomingCrossChainCall>)(static c => c), (UInt256)8, true) { TestName = "NativeValueDiffers" },
        Delivery(static c => c with { SourceRollup = 2 }, "SourceNotL1"),
        Delivery(static c => c with { Entries = [] }, "NoEntry"),
        Delivery(static c => c with { Entries = [c.Entries[0], c.Entries[0]] }, "TwoEntries"),
        Delivery(static c => c with { StaticEntries = [new L2StaticExecutionEntry(Word(1), [], Word(2), true, [])] }, "StaticEntry"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { IncomingCalls = [] }] }, "NoIncomingCall"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { Success = false }] }, "EntryFailed"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { ExpectedOutgoingCalls = [new ExpectedCall(Word(1), [], Word(2), true, [])] }] }, "ExpectsOutgoingCalls"),
        Delivery(static c => c with { Destination = L1Sender }, "DestinationDiffers"),
        Delivery(static c => c with { Data = [9] }, "DataDiffers"),
        Delivery(static c => c with { SourceAddress = L2Contract }, "SourceAddressDiffers"),
        Delivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { Gas = 1 }), "InnerWithGas"),
        Delivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { IsStatic = true }), "InnerStatic"),
        Delivery(static c => WithInner(c, c.Entries[0].IncomingCalls[0] with { RevertNextNCalls = 1 }), "InnerRevertingNext"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { ProxyEntryHash = Word(1) }] }, "ProxyIsNotTheCallHash"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { RollingHash = Word(1) }] }, "RollingHashDiffers"),
        Delivery(static c => c with { Entries = [c.Entries[0] with { ReturnData = [0xcd] }] }, "ReturnDataNotInRollingHash"),
    ];

    private static TestCaseData[] InboundClaims() =>
    [
        Claim(static (e, u) => (e, u), true, "Valid"),
        Claim(static (e, u) => (e with { DestinationRollupId = 2 }, u), false, "OtherRollup"),
        Claim(static (e, u) => (e with { ProxyEntryHash = Word(1) }, u), false, "OtherCall"),
        Claim(static (e, u) => (e with { ReturnData = [0xcd] }, u), false, "OtherResult"),
        Claim(static (e, u) => (e with { RollingHash = Word(1) }, u), false, "RollingHashNotTheL1Seed"),
        Claim(static (e, u) => (e, u with { EtherDelta = Int256.Int256.Zero }), false, "NotCredited"),
        Claim(static (e, u) => (e, u with { EtherDelta = new Int256.Int256(-7) }), false, "Debited"),
    ];

    private static TestCaseData[] OutboundClaims() =>
    [
        Outbound(static (e, u) => (e, u), 0, true, "Valid"),
        Outbound(static (e, u) => (e, u), 1, false, "EventWithGas"),
        Outbound(static (e, u) => (e with { DestinationRollupId = 2 }, u), 0, false, "OtherRollup"),
        Outbound(static (e, u) => (e with { Calls = [e.Calls[0] with { SourceRollupId = 2 }] }, u), 0, false, "CallFromOtherRollup"),
        Outbound(static (e, u) => (e with { Calls = [e.Calls[0] with { Data = [9] }] }, u), 0, false, "OtherCall"),
        Outbound(static (e, u) => (e with { Calls = [] }, u), 0, false, "NoCall"),
        Outbound(static (e, u) => (e with { ReturnData = [0xcd] }, u), 0, false, "ResultNotInRollingHash"),
        Outbound(static (e, u) => (e, u with { EtherDelta = new Int256.Int256(3) }), 0, false, "CreditedInsteadOfDebited"),
        Outbound(static (e, u) => (e, u with { EtherDelta = Int256.Int256.Zero }), 0, false, "NotDebited"),
    ];

    private static TestCaseData[] BadBindings() =>
    [
        Binding(static s => s, [1], [true, false, true, true], 2, "FewerEffectTransactions"),
        Binding(static s => s, [1, 3], [true, true, true, true], 2, "OutboundEntryAtSystemTransaction"),
        Binding(static s => s, [1, 3], [true, false, true, false], 2, "InboundEntryAtUserTransaction"),
        Binding(static s => s, [1, 3], [true, false, true, true], 9, "AnchorDoesNotEndAtTheParent"),
        Binding(static s => (s.Item1, s.Item2, s.Item3[..1]), [1, 3], [true, false, true, true], 2, "MissingCheckpoint"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0], s.Item3[1] with { TransactionIndex = 2 }]), [1, 3], [true, false, true, true], 2, "CheckpointAtOtherTransaction"),
        Binding(static s => (s.Item1, s.Item2, [s.Item3[0], s.Item3[1] with { BlockHash = Keccak.Zero }]), [1, 3], [true, false, true, true], 2, "ClaimsOtherCandidateBlock"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[1], s.Item1.Entries[1], s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3], [true, false, true, true], 2, "LeadingEntryNotAnchor"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[0], s.Item1.Entries[0], s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3], [true, false, true, true], 2, "SecondAnchor"),
        Binding(static s => (s.Item1 with { Entries = [s.Item1.Entries[0], s.Item1.Entries[1] with { Success = false }, s.Item1.Entries[2]] }, s.Item2, s.Item3), [1, 3], [true, false, true, true], 2, "InvalidEffect"),
        Binding(static s => (s.Item1, [s.Item2[0] with { EtherDelta = Int256.Int256.One }, s.Item2[1], s.Item2[2]], s.Item3), [1, 3], [true, false, true, true], 2, "AnchorMovesEther"),
    ];

    private static (PostBatch Batch, StateUpdate[] Updates, EezTransactionCheckpoint[] Checkpoints) EffectBatch()
    {
        StateUpdate[] updates = [Update(1, 2), Update(2, 3), Update(3, 4)];
        ExecutionEntry anchor = new([updates[0]], default, [], [], RollingHash.SeedL1([new StateCommitment(RollupId, Word(1))], default), RollupId, true, []);
        ExecutionEntry outbound = new([updates[1]], default, [new CrossChainCall(0, false, 0, L2Contract, RollupId, L1Sender, 0, [])], [], Word(5), RollupId, true, []);
        ExecutionEntry inbound = new([updates[2]], Word(6), [], [], Word(7), RollupId, true, []);
        PostBatch batch = RecordedBatch() with { Entries = [anchor, outbound, inbound] };
        return (batch, updates, [new EezTransactionCheckpoint(1, Keccak.Zero, new Hash256(Word(3))), new EezTransactionCheckpoint(3, Keccak.Zero, new Hash256(Word(4)))]);
    }

    private static (IncomingCrossChainCall Call, ValueHash256 CallHash) Delivery(UInt256 value, byte[] returnData)
    {
        byte[] data = [1, 2, 3];
        CrossChainCall inner = new(0, false, 0, L1Sender, 0, L2Contract, value, data);
        ValueHash256 callHash = CrossChainCallHash.Compute(false, L1Sender, 0, L2Contract, RollupId, value, 0, data);
        ValueHash256 rolling = RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.SeedL2(callHash), callHash), true, returnData);
        L2ExecutionEntry entry = new(callHash, [inner], [], rolling, true, returnData);
        return (new IncomingCrossChainCall(L2Contract, value, data, L1Sender, 0, [entry], []), callHash);
    }

    private static ExecutionEntry InboundEntry(InboundObservation observation, out StateUpdate update)
    {
        update = Update(1, 2) with { EtherDelta = new Int256.Int256(7) };
        ValueHash256 rolling = RollingHash.SeedL1([new StateCommitment(RollupId, update.CurrentState)], observation.CallHash);
        return new ExecutionEntry([update], observation.CallHash, [], [], rolling, RollupId, true, observation.ReturnData);
    }

    private static ExecutionEntry OutboundEntry(Address source, out StateUpdate update, out ValueHash256 callHash)
    {
        CrossChainCall call = new(0, false, 0, source, RollupId, L1Sender, 3, [4, 5]);
        update = Update(1, 2) with { EtherDelta = new Int256.Int256(-3) };
        callHash = CrossChainCallHash.Compute(false, source, RollupId, L1Sender, 0, 3, 0, call.Data);
        byte[] returnData = [0xee];
        ValueHash256 rolling = RollingHash.CallEnd(
            RollingHash.CallBegin(RollingHash.SeedL1([new StateCommitment(RollupId, update.CurrentState)], default), callHash), true, returnData);
        return new ExecutionEntry([update], default, [call], [], rolling, RollupId, true, returnData);
    }

    private static IncomingCrossChainCall WithInner(IncomingCrossChainCall call, CrossChainCall inner) =>
        call with { Entries = [call.Entries[0] with { IncomingCalls = [inner] }] };

    private static TestCaseData Case(Func<PostBatch, PostBatch> mutate, string name) => new(mutate) { TestName = name };

    private static TestCaseData Delivery(Func<IncomingCrossChainCall, IncomingCrossChainCall> mutate, string name) =>
        new(mutate, (UInt256)7, true) { TestName = name };

    private static TestCaseData Claim(Func<ExecutionEntry, StateUpdate, (ExecutionEntry, StateUpdate)> mutate, bool authorized, string name) =>
        new(mutate) { ExpectedResult = authorized, TestName = name };

    private static TestCaseData Outbound(Func<ExecutionEntry, StateUpdate, (ExecutionEntry, StateUpdate)> mutate, ulong eventCallGas, bool authorized, string name) =>
        new(mutate, eventCallGas) { ExpectedResult = authorized, TestName = name };

    private static TestCaseData Binding(
        Func<(PostBatch, StateUpdate[], EezTransactionCheckpoint[]), (PostBatch, StateUpdate[], EezTransactionCheckpoint[])> mutate,
        int[] effectTransactions, bool[] systemTransactions, ulong preSettling, string name) =>
        new(mutate, effectTransactions, systemTransactions, preSettling) { TestName = name };

    private static StateUpdate Update(ulong from, ulong to) => new(RollupId, Word(from), Word(to), Int256.Int256.Zero);

    private static PostBatch RecordedBatch() =>
        EezCalldata.DecodePostAndVerifyBatch(Bytes.FromHexString(File.ReadAllText(StatelessFixtures.PathOf(Window84, "postbatch.hex")).Trim()));

    private static ValueHash256 Word(ulong value)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), value);
        return new ValueHash256(bytes);
    }
}
