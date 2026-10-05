// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using Snappier;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Sync;

[HardTimeout(60_000)]
public class GloasBlockImporterTests
{
    private const ulong ForkSlot = 32;
    private SignedGloasChain _chain = null!;

    [SetUp]
    public void SetUp() => _chain = new();

    /// <summary>Fork upgrades alias Fulu arrays; import must copy the parent so fork choice retains its original state.</summary>
    [Test]
    public void First_gloas_block_crosses_the_fork_on_a_copy_and_is_stored_in_its_own_shape()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        BeaconChainStore store = _chain.CreateStore();
        BlockImporter importer = _chain.CreateImporter(engine, store: store);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);

        BlockImportResult result = importer.Import(first.Forked, first.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.IsKnown(first.Root), Is.True);
        Assert.That(store.TryGetForkedBlock(first.Root, out ForkedSignedBeaconBlock? stored) ? stored : null, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>());
        Assert.That(SszRoots.HashTreeRoot(_chain.AnchorState), Is.EqualTo(_chain.AnchorBlock.Message!.StateRoot), "the Fulu lineage state is untouched by the crossing");
        Assert.That(engine.HasAnsweredNewPayload, Is.False, "a Gloas block carries only a bid; its payload reaches the engine in the envelope");
    }

    [Test]
    public void Block_whose_shape_is_not_the_fork_of_its_slot_is_invalid_before_any_state_is_touched()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = _chain.CreateImporter(engine);
        SignedBeaconBlock fuluShaped = new() { Message = new BeaconBlock { Slot = ForkSlot, ParentRoot = _chain.AnchorRoot }, Signature = default };

        BlockImportResult result = importer.Import(new ForkedSignedBeaconBlock.OfFulu(fuluShaped), Hash(0x5A), verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
        Assert.That(engine.HasAnsweredNewPayload, Is.False);
    }

    [TestCase(false, ExecutionStatus.Valid, ForkSlot + 1, TestName = "Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(False,Valid)")]
    [TestCase(false, ExecutionStatus.Optimistic, ForkSlot + 1, TestName = "Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(False,Optimistic)")]
    [TestCase(true, ExecutionStatus.Valid, ForkSlot + 1, TestName = "Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(True,Valid)")]
    [TestCase(true, ExecutionStatus.Optimistic, ForkSlot + 1, TestName = "Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(True,Optimistic)")]
    [TestCase(true, ExecutionStatus.Valid, 3 * ForkSlot, TestName = "Full_child_past_its_parents_lookahead_is_deferred_until_the_envelope")]
    public void Child_waits_for_its_parents_envelope_only_when_it_builds_on_the_full_payload(bool full, ExecutionStatus envelopeVerdict, ulong childSlot)
    {
        SignedGloasChain.EnvelopeEngine engine = new() { EnvelopeVerdict = envelopeVerdict };
        BeaconChainStore store = _chain.CreateStore();
        BlockImporter importer = _chain.CreateImporter(engine, store: store);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(first, childSlot, full, 0xA2);
        Import(importer, first);

        BlockImportResult beforeEnvelope = importer.Import(child.Forked, child.Root, verifySignatures: true);
        bool storedBeforeEnvelope = store.HasBlock(child.Root);
        bool knownBeforeEnvelope = importer.IsKnown(child.Root);
        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);
        BlockImportResult afterEnvelope = full ? importer.Import(child.Forked, child.Root, verifySignatures: true) : BlockImportResult.AlreadyKnown;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(beforeEnvelope, Is.EqualTo(full ? BlockImportResult.ParentPayloadUnverified : BlockImportResult.Imported));
        Assert.That(knownBeforeEnvelope, Is.EqualTo(!full), "a deferred child is not in fork choice");
        Assert.That(storedBeforeEnvelope, Is.EqualTo(!full), "a deferred child is not stored");
        Assert.That(envelope, Is.EqualTo(envelopeVerdict == ExecutionStatus.Valid ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.Optimistic));
        Assert.That(afterEnvelope, Is.EqualTo(full ? BlockImportResult.Imported : BlockImportResult.AlreadyKnown), "an optimistic payload is recorded as an optimistic block is imported");
    }

    [TestCase(ExecutionStatus.Invalid, true, ExecutionPayloadEnvelopeImportResult.Invalid)]
    [TestCase(ExecutionStatus.Valid, false, ExecutionPayloadEnvelopeImportResult.DataUnavailable)]
    public void Envelope_that_does_not_verify_leaves_the_full_child_waiting(ExecutionStatus envelopeVerdict, bool dataAvailable, ExecutionPayloadEnvelopeImportResult expected)
    {
        SignedGloasChain.EnvelopeEngine engine = new() { EnvelopeVerdict = envelopeVerdict };
        BlockImporter importer = _chain.CreateImporter(engine, isEnvelopeDataAvailable: (_, _) => dataAvailable);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(envelope, Is.EqualTo(expected));
        Assert.That(importer.Import(child.Forked, child.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
    }

    [Test]
    public void Envelope_already_verified_or_for_an_unknown_block_never_reaches_the_engine()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = _chain.CreateImporter(engine);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block neverImported = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        ExecutionPayloadEnvelopeImportResult firstTime = importer.ImportEnvelope(first.Envelope);
        ExecutionPayloadEnvelopeImportResult secondTime = importer.ImportEnvelope(first.Envelope);
        ExecutionPayloadEnvelopeImportResult unknown = importer.ImportEnvelope(neverImported.Envelope);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(firstTime, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
        Assert.That(secondTime, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown));
        Assert.That(unknown, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
        Assert.That(engine.EnvelopeCalls, Is.EqualTo(1));
    }

    [TestCase(false, TestName = "Head_execution_hash_moves_to_the_bid_block_hash_once_the_envelope_verifies")]
    [TestCase(true, TestName = "Head_hash_of_the_same_head_flips_from_empty_to_full_when_its_payload_is_verified")]
    public void Head_execution_hash_moves_to_the_bid_block_hash_once_the_envelope_verifies(bool clockAtBlock)
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        ForkChoiceSnapshotHolder snapshots = new();
        SlotClock? clock = clockAtBlock ? new SlotClock(_chain.Spec, new ManualTimestamper(SlotStart(_chain, ForkSlot).AddSeconds(1))) : null;
        BlockImporter importer = _chain.CreateImporter(engine, snapshots: snapshots, clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);

        HeadView beforeEnvelope = importer.ComputeHead();
        Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture: the payload is verified");
        HeadView afterEnvelope = importer.ComputeHead();
        Hash256 anchorPayloadHash = _chain.AnchorBlock.Message!.Body!.ExecutionPayload!.BlockHash!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((beforeEnvelope.HeadRoot, afterEnvelope.HeadRoot), Is.EqualTo((first.Root, first.Root)), "fixture: the head does not move");
        Assert.That((beforeEnvelope.HeadPayloadFull, afterEnvelope.HeadPayloadFull), Is.EqualTo((false, true)), "only a verified payload makes the head FULL");
        Assert.That(beforeEnvelope.HeadExecutionHash, Is.EqualTo(first.Bid.ParentBlockHash));
        Assert.That(afterEnvelope.HeadExecutionHash, Is.EqualTo(first.Bid.BlockHash));
        Assert.That(afterEnvelope.FinalizedExecutionHash, Is.EqualTo(anchorPayloadHash), "a Fulu checkpoint keeps its own payload hash");
        Assert.That(snapshots.Current!.Nodes.Single(n => n.Root == first.Root).ExecutionStatus, Is.EqualTo(ExecutionStatus.Valid));
        Assert.That(snapshots.Current!.Nodes.Single(n => n.Root == first.Root).PayloadValid, Is.True);
    }

    [Test]
    public void Head_hash_follows_the_payload_status_the_ptc_votes_resolve([Values] bool ptcVotedTimely)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot).AddSeconds(1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);
        Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture: the head's payload is verified");
        if (ptcVotedTimely)
        {
            importer.ComputeHead();
            foreach (ulong member in first.PostState.GetPtc(ForkSlot, _chain.Spec).Indices!.Distinct())
            {
                Assert.That(importer.OnGossipPayloadAttestation(PtcVote(first, member, payloadPresent: true)), Is.True, "fixture: a signed vote of a PTC member");
            }
        }

        timestamper.Set(SlotStart(_chain, ForkSlot + 1).AddSeconds(1));
        SignedGloasChain.Block boosted = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, boosted);
        importer.OnInvalidExecutionPayload(boosted.Root, latestValidHash: null);

        HeadView head = importer.ComputeHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head.HeadRoot, Is.EqualTo(first.Root), "fixture: the invalidated block leaves the tree");
        Assert.That(head.HeadExecutionHash, Is.EqualTo(ptcVotedTimely ? first.Bid.BlockHash : first.Bid.ParentBlockHash));
        Assert.That(head.HeadPayloadFull, Is.EqualTo(ptcVotedTimely), "the payload status the envelope server reads is the one get_head resolved");
    }

    [Test]
    public void Block_body_payload_attestations_reach_fork_choice_on_import([Values] bool inBody)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot).AddSeconds(1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);
        Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture: the head's payload is verified");
        PayloadAttestation vote = PtcAttestation(
            first.PostState,
            new PayloadAttestationData { BeaconBlockRoot = first.Root, Slot = ForkSlot, PayloadPresent = true, BlobDataAvailable = true },
            [.. Enumerable.Range(0, (int)Presets.PtcSize)],
            sign: true);

        timestamper.Set(SlotStart(_chain, ForkSlot + 1).AddSeconds(1));
        SignedGloasChain.Block boosted = _chain.Next(first, ForkSlot + 1, full: false, 0xA2, payloadAttestations: inBody ? [vote] : []);
        Import(importer, boosted);
        importer.OnInvalidExecutionPayload(boosted.Root, latestValidHash: null);

        HeadView head = importer.ComputeHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head.HeadRoot, Is.EqualTo(first.Root), "fixture: the invalidated block leaves the tree");
        Assert.That(head.HeadExecutionHash, Is.EqualTo(inBody ? first.Bid.BlockHash : first.Bid.ParentBlockHash));
    }

    [Test]
    public async Task Gossip_aggregate_claiming_a_payload_in_its_blocks_slot_charges_the_delivering_peer([Values] bool finalizedAncestor, [Values] bool validSignature)
    {
        BeaconChainStore store = _chain.CreateStore();
        SlotClock clock = ClockAt(_chain, ForkSlot, millisecondsEarly: 0);
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), snapshots: snapshots, store: store, clock: clock);
        SignedGloasChain.Block block = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, block);
        SignedGloasChain.Block sibling = _chain.Next(null, ForkSlot, full: false, 0xA2);
        if (!finalizedAncestor)
        {
            Import(importer, sibling);
            Finalize(importer, new CheckpointRef(1, sibling.Root));
        }
        SignedAggregateAndProofGloas aggregate = SignedAggregate(block, new AttestationData
        {
            Slot = ForkSlot,
            Index = 1,
            BeaconBlockRoot = block.Root,
            Source = new Checkpoint { Epoch = 0, Root = _chain.AnchorRoot },
            Target = new Checkpoint { Epoch = 1, Root = block.Root },
        });
        if (!validSignature)
        {
            aggregate.Signature = Sign(ValidatorKey((int)(aggregate.Message!.AggregatorIndex + 1) % ValidatorCount),
                Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(aggregate.Message), block.PostState.GetDomain(DomainType.AggregateAndProof, 1)));
        }
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        runner.GetHead();
        Assert.That(() => runner.OnAggregateAndProof(aggregate),
            Throws.TypeOf<ForkChoiceException>().With.Message.EqualTo(!validSignature ? "Aggregator signature is invalid" : finalizedAncestor
                ? $"Attestation for slot {ForkSlot} votes for the payload of a block from its own slot"
                : $"Aggregate head block {block.Root} does not descend from the finalized checkpoint {new CheckpointRef(1, sibling.Root)}"),
            "finalized ancestry must be checked before the same-slot payload claim can charge a peer");
        ColumnGossipRouter headers = new(_chain.Spec, clock, LimboLogs.Instance, forkChoice: snapshots);
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance, store, headers: headers);
        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, ForkSlot, GossipTopics.BeaconAggregateAndProof,
            Snappy.CompressToArray(SignedAggregateAndProofGloas.Encode(aggregate)), verdict => new BeaconSyncOrchestrator.GossipGloasAggregateItem(aggregate, verdict),
            !validSignature || finalizedAncestor ? MessageValidity.Rejected : MessageValidity.Ignored, router, gloas: true);
    }

    [Test]
    public async Task Stale_aggregate_for_finalized_block_rejects_out_of_range_committee_before_timing([Values(1UL, ulong.MaxValue)] ulong targetEpoch)
    {
        SignedGloasChain.Block block = _chain.Next(null, ForkSlot, full: false, 0xA1);
        BeaconChainStore store = _chain.CreateStore();
        store.PutForkedBlock(block.Root, block.Forked);
        ulong currentSlot = 3 * ForkSlot;
        SlotClock clock = ClockAt(_chain, currentSlot, millisecondsEarly: 0);
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), snapshots: snapshots, store: store, clock: clock, gloasAnchor: block);
        importer.OnSlotTick(currentSlot);
        importer.ComputeHead();
        SignedAggregateAndProofGloas aggregate = SignedAggregate(block);
        aggregate.Message!.Aggregate!.Data!.Index = 1;
        aggregate.Message.Aggregate.Data.Target!.Epoch = targetEpoch;
        aggregate.Message.Aggregate.CommitteeBits!.SetAll(false);
        aggregate.Message.Aggregate.CommitteeBits[1] = true;
        ColumnGossipRouter headers = new(_chain.Spec, clock, LimboLogs.Instance, forkChoice: snapshots);
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance, store, headers: headers);

        Assert.That(headers.HasFinalizedAncestor(block.Root), Is.True, "the same-slot payload claim must not bypass committee validation for a held finalized block");
        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, currentSlot, GossipTopics.BeaconAggregateAndProof,
            Snappy.CompressToArray(SignedAggregateAndProofGloas.Encode(aggregate)), verdict => new BeaconSyncOrchestrator.GossipGloasAggregateItem(aggregate, verdict),
            MessageValidity.Rejected, router, gloas: true);
    }

    public enum GossipPtcVote
    {
        Signed,
        BadSignature,
        NotInPtc,
        UnknownBlock,
        BlockAtAnotherSlot,
        PreviousSlot,
    }

    [TestCase(GossipPtcVote.Signed, true)]
    [TestCase(GossipPtcVote.BadSignature, false)]
    [TestCase(GossipPtcVote.NotInPtc, false)]
    [TestCase(GossipPtcVote.UnknownBlock, false)]
    [TestCase(GossipPtcVote.BlockAtAnotherSlot, false)]
    [TestCase(GossipPtcVote.PreviousSlot, false)]
    public async Task Gossip_payload_attestation_is_accepted_only_when_fork_choice_applies_it(GossipPtcVote vote, bool accepted)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot + 1).AddSeconds(1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block second = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first, second);
        ulong[] ptc = second.PostState.GetPtc(ForkSlot + 1, _chain.Spec).Indices!;
        ulong outsider = Enumerable.Range(0, ValidatorCount).Select(static i => (ulong)i).First(i => !ptc.Contains(i));
        PayloadAttestationMessage message = vote == GossipPtcVote.PreviousSlot
            ? PtcVote(first, first.PostState.GetPtc(ForkSlot, _chain.Spec).Indices![0], payloadPresent: true)
            : PtcVote(second, vote == GossipPtcVote.NotInPtc ? outsider : ptc[0], payloadPresent: true);
        switch (vote)
        {
            case GossipPtcVote.BadSignature:
                message.Data!.PayloadPresent = false;
                break;
            case GossipPtcVote.UnknownBlock:
                message.Data!.BeaconBlockRoot = Hash(0x5A);
                break;
            case GossipPtcVote.BlockAtAnotherSlot:
                // Signed for the current slot, but naming the previous slot's block: fork choice would record nothing and check nothing.
                message.Data!.BeaconBlockRoot = first.Root;
                message.Signature = default;
                break;
        }

        long refusedBefore = RefusedByForkChoice("gossip_payload_attestation");
        MessageValidity expected = accepted ? MessageValidity.Accepted
            : vote is GossipPtcVote.BadSignature or GossipPtcVote.NotInPtc ? MessageValidity.Rejected : MessageValidity.Ignored;
        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, ForkSlot + 1, GossipTopics.PayloadAttestationMessage,
            Snappy.CompressToArray(PayloadAttestationMessage.Encode(message)), verdict => new BeaconSyncOrchestrator.GossipPayloadAttestationItem(message, verdict), expected);
        Assert.That(RefusedByForkChoice("gossip_payload_attestation") - refusedBefore, Is.EqualTo(accepted ? 0 : 1), "a refused vote is counted");
    }

    [Test]
    public async Task Gossip_payload_attestation_uses_head_membership_without_charging_for_block_state_refusal([Values] bool headMember)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot).AddSeconds(1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        SignedGloasChain.Block a = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block b = _chain.Next(null, ForkSlot, full: false, 0xB1);
        Import(importer, a, b);
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        SignedGloasChain.Block voted = runner.GetHead() == a.Root ? b : a;
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        BeaconStateGloas headState = states.GetGloasBlockState(runner.GetHead())!;
        BeaconStateGloas blockState = states.GetGloasBlockState(voted.Root)!;
        ulong member = headState.GetPtc(ForkSlot, _chain.Spec).Indices![0];
        ulong outsider = Enumerable.Range(0, ValidatorCount).Select(static i => (ulong)i).First(i => !headState.GetPtc(ForkSlot, _chain.Spec).Indices!.Contains(i));
        int ptcIndex = (int)(Presets.SlotsPerEpoch + ForkSlot % Presets.SlotsPerEpoch);
        // Different retained committees isolate head-state gossip checks from voted-block fork choice.
        blockState.PtcWindow![ptcIndex] = new PayloadTimelinessCommittee { Indices = Enumerable.Repeat(outsider, (int)Presets.PtcSize).ToArray() };
        PayloadAttestationMessage message = PtcVote(voted, headMember ? member : outsider, payloadPresent: true);
        ForkChoiceException refusal = Assert.Throws<ForkChoiceException>(() => runner.OnPayloadAttestationMessage(message))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusal.RejectGossip, Is.EqualTo(!headMember), "only a head-state gossip failure may penalize the relay");
            Assert.That(refusal.Message, Does.Contain(headMember ? "outside that slot's PTC" : "outside the head state's PTC"));
        }
        long refusedBefore = RefusedByForkChoice("gossip_payload_attestation");

        await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, ForkSlot, GossipTopics.PayloadAttestationMessage,
            Snappy.CompressToArray(PayloadAttestationMessage.Encode(message)), verdict => new BeaconSyncOrchestrator.GossipPayloadAttestationItem(message, verdict),
            headMember ? MessageValidity.Ignored : MessageValidity.Rejected);
        Assert.That(RefusedByForkChoice("gossip_payload_attestation") - refusedBefore, Is.EqualTo(1), "neither refusal may apply a vote");
    }

    [Test]
    public void Forged_gossip_payload_attestations_under_one_member_cost_fork_choice_a_bounded_number_of_verifies()
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot + 1).AddSeconds(1));
        SlotClock clock = new(_chain.Spec, timestamper);
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block second = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first, second);
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance);
        router.PayloadAttestationMessageReceived += (vote, _) => importer.OnGossipPayloadAttestation(vote);
        importer.ComputeHead();
        ulong member = second.PostState.GetPtc(ForkSlot + 1, _chain.Spec).Indices![0];
        PayloadAttestationMessage genuine = PtcVote(second, member, payloadPresent: true);
        const int forgeries = 40;

        long refusedBefore = RefusedByForkChoice("gossip_payload_attestation");
        for (int i = 0; i < forgeries; i++)
        {
            PayloadAttestationMessage forged = PtcVote(second, member, payloadPresent: true);
            forged.Signature = new BlsSignature([.. Enumerable.Repeat((byte)(i + 1), BlsSignature.Length)]);
            router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(forged)));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(RefusedByForkChoice("gossip_payload_attestation") - refusedBefore, Is.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts), "the pair's verify attempts");
        Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(forgeries - GossipRouter.PayloadAttestationVerifyAttempts));
        Assert.That(importer.OnGossipPayloadAttestation(genuine), Is.True, "fixture: the genuine vote verifies when it reaches fork choice");
    }

    [Test]
    public void Genuine_gossip_payload_attestation_after_a_forgery_reaches_fork_choice()
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot + 1).AddSeconds(1));
        SlotClock clock = new(_chain.Spec, timestamper);
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block second = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first, second);
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance);
        int accepted = 0;
        importer.ComputeHead();
        router.PayloadAttestationMessageReceived += (vote, _) =>
        {
            if (importer.OnGossipPayloadAttestation(vote) == true)
            {
                accepted++;
                router.MarkPayloadAttestationVerified(vote);
            }
        };
        ulong member = second.PostState.GetPtc(ForkSlot + 1, _chain.Spec).Indices![0];
        PayloadAttestationMessage forged = PtcVote(second, member, payloadPresent: true);
        forged.Signature = new BlsSignature([.. Enumerable.Repeat((byte)1, BlsSignature.Length)]);

        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(forged)));
        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(PtcVote(second, member, payloadPresent: true))));

        Assert.That(accepted, Is.EqualTo(1), "the genuine vote verified after the forgery was refused");
    }

    [Test]
    public void Head_ptc_is_the_head_states_committee_and_null_where_it_cannot_be_read()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importer.GetPtc(first.Root, ForkSlot), Is.EqualTo(first.PostState.GetPtc(ForkSlot, _chain.Spec).Indices));
        Assert.That(importer.GetPtc(Hash(0x5A), ForkSlot), Is.Null, "unknown head");
        Assert.That(importer.GetPtc(first.Root, ForkSlot + 10 * Presets.SlotsPerEpoch), Is.Null, "outside the state's window");
    }

    [Test]
    public async Task Worker_accepts_votes_after_a_slashing_without_an_intervening_tick([Values] bool gloasSlashing, [Values] bool payloadVote)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot).AddSeconds(1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        SignedGloasChain.Block block = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, block);
        AttesterSlashing slashing = SignedSlashing(_chain, block);
        List<MessageValidity> slashingVerdicts = [];
        GossipVerdict slashingVerdict = new(validity => { slashingVerdicts.Add(validity); return true; }, null);
        BeaconSyncOrchestrator.WorkItem preceding = gloasSlashing
            ? new BeaconSyncOrchestrator.GossipGloasAttesterSlashingItem(new AttesterSlashingGloas
            {
                Attestation1 = new IndexedAttestationGloas { AttestingIndices = slashing.Attestation1!.AttestingIndices, Data = slashing.Attestation1.Data, Signature = slashing.Attestation1.Signature },
                Attestation2 = new IndexedAttestationGloas { AttestingIndices = slashing.Attestation2!.AttestingIndices, Data = slashing.Attestation2.Data, Signature = slashing.Attestation2.Signature },
            }, slashingVerdict)
            : new BeaconSyncOrchestrator.GossipAttesterSlashingItem(slashing, slashingVerdict);

        if (payloadVote)
        {
            PayloadAttestationMessage vote = PtcVote(block, block.PostState.GetPtc(ForkSlot, _chain.Spec).Indices![0], payloadPresent: true);
            await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, ForkSlot, GossipTopics.PayloadAttestationMessage,
                Snappy.CompressToArray(PayloadAttestationMessage.Encode(vote)), verdict => new BeaconSyncOrchestrator.GossipPayloadAttestationItem(vote, verdict),
                MessageValidity.Accepted, preceding: preceding);
        }
        else
        {
            SignedAggregateAndProofGloas aggregate = SignedAggregate(block);
            await BeaconSyncOrchestratorTests.AssertOperationVerdictAsync(importer, ForkSlot, GossipTopics.BeaconAggregateAndProof,
                Snappy.CompressToArray(SignedAggregateAndProofGloas.Encode(aggregate)), verdict => new BeaconSyncOrchestrator.GossipGloasAggregateItem(aggregate, verdict),
                MessageValidity.Accepted, preceding: preceding);
        }

        Assert.That(slashingVerdicts, Is.EqualTo(new[] { MessageValidity.Accepted }), "the slashing must clear the cached head before the following vote is consumed");
    }

    [Test]
    public async Task Worker_accepts_gossip_after_a_skipped_tick_with_a_real_importer([Values] SkippedTickOperation operation)
    {
        ManualTimestamper timestamper = new(SlotStart(_chain, ForkSlot));
        SlotClock clock = new(_chain.Spec, timestamper);
        BeaconChainStore store = _chain.CreateStore();
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), store: store, clock: clock);
        SignedGloasChain.Block block = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, block);
        timestamper.Set(SlotStart(_chain, ForkSlot + 1));
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance, store);
        string topic;
        byte[] payload;
        if (operation == SkippedTickOperation.Aggregate)
        {
            topic = GossipTopics.BeaconAggregateAndProof;
            payload = SignedAggregateAndProofGloas.Encode(SignedAggregate(block));
        }
        else
        {
            topic = GossipTopics.AttesterSlashing;
            AttesterSlashing slashing = SignedSlashing(_chain, block);
            payload = operation == SkippedTickOperation.FuluSlashing ? AttesterSlashing.Encode(slashing) : AttesterSlashingGloas.Encode(new AttesterSlashingGloas
            {
                Attestation1 = new IndexedAttestationGloas { AttestingIndices = slashing.Attestation1!.AttestingIndices, Data = slashing.Attestation1.Data, Signature = slashing.Attestation1.Signature },
                Attestation2 = new IndexedAttestationGloas { AttestingIndices = slashing.Attestation2!.AttestingIndices, Data = slashing.Attestation2.Data, Signature = slashing.Attestation2.Signature },
            });
        }

        await BeaconSyncOrchestratorTests.AssertOperationAfterSkippedTickAsync(importer, ForkSlot + 1, router, topic,
            Snappy.CompressToArray(payload), gloas: operation != SkippedTickOperation.FuluSlashing);
    }

    public enum SkippedTickOperation { Aggregate, FuluSlashing, GloasSlashing }

    internal static AttesterSlashing SignedSlashing(SignedGloasChain chain, SignedGloasChain.Block block)
    {
        AttestationData first = new() { Slot = 1, BeaconBlockRoot = chain.AnchorRoot, Source = chain.AnchorState.CurrentJustifiedCheckpoint, Target = new Checkpoint { Root = chain.AnchorRoot } };
        AttestationData second = new() { Slot = 1, BeaconBlockRoot = Hash(0xEE), Source = first.Source, Target = first.Target };
        return new()
        {
            Attestation1 = new IndexedAttestation { AttestingIndices = [1], Data = first, Signature = SignAs(1, SszRoots.HashTreeRoot(first), DomainType.BeaconAttester, 0) },
            Attestation2 = new IndexedAttestation { AttestingIndices = [1], Data = second, Signature = SignAs(1, SszRoots.HashTreeRoot(second), DomainType.BeaconAttester, 0) },
        };

        BlsSignature SignAs(int validator, Hash256 root, ReadOnlySpan<byte> domain, ulong epoch) =>
            Sign(ValidatorKey(validator), Domains.ComputeSigningRoot(root, block.PostState.GetDomain(domain, epoch)));
    }

    internal static SignedAggregateAndProofGloas SignedAggregate(SignedGloasChain.Block block, AttestationData? data = null)
    {
        CommitteeCache committees = new EpochCache().GetCommitteeCache(block.PostState, 1);
        data ??= new() { Slot = ForkSlot, BeaconBlockRoot = block.Root, Source = block.PostState.CurrentJustifiedCheckpoint, Target = new Checkpoint { Epoch = 1, Root = block.Root } };
        byte[] slotRoot = new byte[32];
        BitConverter.TryWriteBytes(slotRoot, ForkSlot);
        int[] committee = committees.GetBeaconCommittee(ForkSlot, 0).ToArray();
        int member = committee.First(index => BeaconStateAccessors.IsAggregator(committee.Length, SignAs(index, new Hash256(slotRoot), DomainType.SelectionProof, 1)));
        AggregateAndProofGloas message = new()
        {
            AggregatorIndex = (ulong)member,
            SelectionProof = SignAs(member, new Hash256(slotRoot), DomainType.SelectionProof, 1),
            Aggregate = new AttestationGloas
            {
                Data = data,
                AggregationBits = new BitArray(committee.Length) { [Array.IndexOf(committee, member)] = true },
                CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
                Signature = SignAs(member, SszRoots.HashTreeRoot(data), DomainType.BeaconAttester, 1),
            },
        };
        return new() { Message = message, Signature = SignAs(member, SszRoots.HashTreeRoot(message), DomainType.AggregateAndProof, 1) };

        BlsSignature SignAs(int validator, Hash256 root, ReadOnlySpan<byte> domain, ulong epoch) =>
            Sign(ValidatorKey(validator), Domains.ComputeSigningRoot(root, block.PostState.GetDomain(domain, epoch)));
    }

    private static PayloadAttestationMessage PtcVote(SignedGloasChain.Block block, ulong validatorIndex, bool payloadPresent)
    {
        ulong slot = block.Signed.Message!.Slot;
        PayloadAttestationData data = new() { BeaconBlockRoot = block.Root, Slot = slot, PayloadPresent = payloadPresent, BlobDataAvailable = payloadPresent };
        Hash256 domain = block.PostState.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(slot));
        return new PayloadAttestationMessage
        {
            ValidatorIndex = validatorIndex,
            Data = data,
            Signature = Sign(ValidatorKey((int)validatorIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain)),
        };
    }

    private static DateTime SlotStart(SignedGloasChain chain, ulong slot) => DateTime.UnixEpoch.AddSeconds(chain.Spec.GenesisTime + slot * chain.Spec.SecondsPerSlot);

    [Test]
    public void Justified_gloas_checkpoint_maps_to_its_bid_parent_block_hash()
    {
        ForkCrossingChain fork = ForkCrossingChain.Instance;
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        foreach (ForkCrossingChain.ChainBlock block in (ForkCrossingChain.ChainBlock[])[fork.First, .. fork.Voting])
        {
            Assert.That(importer.Import(new ForkedSignedBeaconBlock.OfGloas(block.Block), block.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        }

        importer.OnSlotTick(3 * _chain.Spec.SlotsPerEpoch);
        HeadView head = importer.ComputeHead();
        ExecutionPayloadBid firstBid = fork.First.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head.Justified.Root, Is.EqualTo(fork.First.Root), "fixture: the epoch-3 pull-up justifies the first Gloas block");
        Assert.That(head.JustifiedExecutionHash, Is.EqualTo(firstBid.ParentBlockHash));
        Assert.That(head.JustifiedExecutionHash, Is.Not.EqualTo(firstBid.BlockHash));
    }

    [Test]
    public void Replayed_full_child_stands_in_for_its_parents_envelope()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = _chain.CreateImporter(engine);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block grandchild = _chain.Next(child, ForkSlot + 2, full: true, 0xA3);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importer.Import(first.Forked, first.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.Import(child.Forked, child.Root, verifySignatures: false), Is.EqualTo(BlockImportResult.Imported));
        Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.AlreadyKnown), "the replayed child recorded its parent's payload");
        Assert.That(importer.Import(grandchild.Forked, grandchild.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "the replayed tip's own payload is still unverified");
        Assert.That(engine.EnvelopeCalls, Is.Zero);
    }

    [Test]
    public void Checkpoint_block_states_outlive_the_per_block_tier([Values] bool skipWholeEpoch)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block epochStart = _chain.Next(null, ForkSlot, full: false, 0xB0);
        SignedGloasChain.Block middle = _chain.Next(epochStart, ForkSlot + 1, full: false, 0xB1);
        SignedGloasChain.Block lastBeforeSkip = _chain.Next(middle, ForkSlot + 2, full: false, 0xB2);
        Import(importer, epochStart, middle, lastBeforeSkip);

        SignedGloasChain.Block tip = lastBeforeSkip;
        SignedGloasChain.Block? lastOfEpoch = null;
        ulong resumeSlot = skipWholeEpoch ? 3 * ForkSlot : 2 * ForkSlot + 1;
        ulong nextEpochStart = (resumeSlot / ForkSlot + 1) * ForkSlot;
        for (ulong slot = resumeSlot; slot <= resumeSlot + 3 * ForkSlot; slot++)
        {
            tip = _chain.Next(tip, slot, full: false, (byte)(slot + 0x60));
            Import(importer, tip);
            if (slot == nextEpochStart - 1)
            {
                lastOfEpoch = tip;
            }
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importer.ImportEnvelope(epochStart.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "an epoch's first block");
        Assert.That(importer.ImportEnvelope(lastBeforeSkip.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the checkpoint block of the epoch whose first slot was skipped");
        Assert.That(importer.ImportEnvelope(middle.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock), "a block that is no checkpoint ages out");
        Assert.That(importer.ImportEnvelope(lastOfEpoch!.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock), "the parent of the next epoch's first block is no checkpoint and ages out");
    }

    public enum EvictedParent
    {
        Empty,
        FullVerified,
        FullUnverified,
    }

    public enum HeldBase
    {
        GloasCheckpoint,
        FuluAnchor,
        GloasAnchor,
    }

    [Test]
    public void Sibling_on_an_evicted_gloas_parent_imports_on_a_regenerated_state([Values] EvictedParent parentKind, [Values] HeldBase heldBase)
    {
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        Hash256 anchorStateRoot = SszRoots.HashTreeRoot(_chain.AnchorState);
        SignedGloasChain.Block first = _chain.Next(null, heldBase == HeldBase.FuluAnchor ? ForkSlot + 1 : ForkSlot, full: false, 0xE1);
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)),
            gloasAnchor: heldBase == HeldBase.GloasAnchor ? first : null);
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        if (heldBase != HeldBase.GloasAnchor)
        {
            Import(importer, first);
            Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture");
        }

        ulong parentSlot = first.Signed.Message!.Slot + 1;
        SignedGloasChain.Block parent = _chain.Next(first, parentSlot, full: heldBase != HeldBase.GloasAnchor, 0xE2);
        Import(importer, parent);
        if (parentKind == EvictedParent.FullVerified)
        {
            Assert.That(importer.ImportEnvelope(parent.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "fixture");
        }

        SignedGloasChain.Block tip = parent;
        for (ulong slot = parentSlot + 1; slot <= parentSlot + 2 * ForkSlot + 1; slot++)
        {
            tip = _chain.Next(tip, slot, full: false, (byte)slot);
            Import(importer, tip);
        }

        BeaconStateGloas? heldBefore = states.GetGloasBlockState(parent.Root);
        SignedGloasChain.Block sibling = _chain.Next(parent, parentSlot + 1, full: parentKind != EvictedParent.Empty, 0xE3);
        BlockImportResult result = importer.ImportRequested(sibling.Forked, sibling.Root);
        ExecutionPayloadEnvelopeImportResult? envelope = null;
        BlockImportResult? afterEnvelope = null;
        if (parentKind == EvictedParent.FullUnverified)
        {
            envelope = importer.ImportEnvelope(parent.Envelope);
            afterEnvelope = importer.ImportRequested(sibling.Forked, sibling.Root);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldBefore, Is.Null, "fixture: the parent's state left every tier, and the gossip getter does not regenerate it");
        if (parentKind == EvictedParent.FullUnverified)
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
            Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid), "the regenerated parent state verifies the parent's envelope");
            Assert.That(afterEnvelope, Is.EqualTo(BlockImportResult.Imported));
        }
        else
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        }

        if (heldBase == HeldBase.FuluAnchor)
        {
            Assert.That(SszRoots.HashTreeRoot(_chain.AnchorState), Is.EqualTo(anchorStateRoot), "the replay crosses the fork on a copy of the held Fulu state");
        }
        else
        {
            Assert.That(SszRoots.HashTreeRoot(states.GetGloasBlockState(first.Root)!), Is.EqualTo(first.Signed.Message!.StateRoot), "the replay runs on a copy of the held Gloas state");
        }
        Assert.That(logger.LogList, Has.None.Contains("no longer retained"));
        Assert.That(logger.LogList, Has.None.Contains("Cannot regenerate"));
    }

    [Test]
    public void Fulu_parent_of_a_first_gloas_block_that_skips_the_fork_slot_stays_a_regeneration_base()
    {
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)));
        List<BlockImportResult> fixture = [];
        void ImportFulu(SignedGloasChain.FuluBlock block) => fixture.Add(importer.Import(block.Forked, block.Root, verifySignatures: true));

        ImportFulu(_chain.NextFulu(2, blockHashFill: 0xD0));
        SignedGloasChain.FuluBlock? lastFulu = null;
        for (ulong slot = 1; slot < ForkSlot; slot++)
        {
            lastFulu = _chain.NextFulu(slot, lastFulu, (byte)slot);
            ImportFulu(lastFulu);
        }

        SignedGloasChain.Block first = _chain.NextOnFulu(lastFulu!, ForkSlot + 1, full: false, 0xE1);
        SignedGloasChain.Block parent = _chain.Next(first, ForkSlot + 2, full: false, 0xE2);
        Import(importer, first, parent);

        for (ulong slot = 3; slot < 12; slot++)
        {
            ImportFulu(_chain.NextFulu(slot, blockHashFill: (byte)(0x80 + slot)));
        }

        SignedGloasChain.Block tip = parent;
        for (ulong slot = ForkSlot + 3; slot <= 3 * ForkSlot + 3; slot++)
        {
            tip = _chain.Next(tip, slot, full: false, (byte)slot);
            Import(importer, tip);
        }

        SignedGloasChain.Block sibling = _chain.Next(parent, ForkSlot + 3, full: false, 0xE3);
        BlockImportResult result = importer.ImportRequested(sibling.Forked, sibling.Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture, Is.All.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(logger.LogList, Has.None.Contains("Cannot regenerate"));
    }

    [Test]
    public void Fulu_checkpoint_parent_retained_from_the_lineage_is_a_copy()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.FuluBlock lineage = _chain.NextFulu(ForkSlot - 2);
        SignedGloasChain.FuluBlock late = _chain.NextFulu(ForkSlot - 1, lineage, 0xD1);
        SignedGloasChain.Block first = _chain.NextOnFulu(lineage, ForkSlot + 1, full: false, 0xE1);
        SignedGloasChain.Block sibling = _chain.NextOnFulu(lineage, ForkSlot + 2, full: false, 0xE2);

        BlockImportResult[] fixture =
        [
            importer.Import(lineage.Forked, lineage.Root, verifySignatures: true),
            importer.Import(first.Forked, first.Root, verifySignatures: true),
            importer.Import(late.Forked, late.Root, verifySignatures: false),
        ];
        BlockImportResult result = importer.Import(sibling.Forked, sibling.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(fixture, Is.All.EqualTo(BlockImportResult.Imported), "fixture bug");
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported));
    }

    [Test]
    public void Forged_children_of_evicted_blocks_cost_no_regeneration()
    {
        TestLogger logger = new() { IsInfo = false, IsTrace = false };
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)));
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        List<SignedGloasChain.Block> blocks = [];
        SignedGloasChain.Block? tip = null;
        for (ulong slot = ForkSlot; slot < ForkSlot + 136; slot++)
        {
            // Offset so no fill is the anchor payload's 0x71, which a bid may not repeat as its parent block hash.
            tip = _chain.Next(tip, slot, full: false, (byte)(slot + 0x80));
            Import(importer, tip);
            blocks.Add(tip);
        }

        List<(SignedGloasChain.Block Parent, ForkedSignedBeaconBlock Forged, Hash256 Root)> forged = [];
        for (int i = 1; i < blocks.Count - 2 * (int)ForkSlot - 1; i++)
        {
            if (blocks[i].Signed.Message!.Slot % ForkSlot == 0)
            {
                continue;
            }

            BeaconBlockGloas real = blocks[i + 1].Signed.Message!;
            BeaconBlockGloas message = new() { Slot = real.Slot, ProposerIndex = real.ProposerIndex, ParentRoot = real.ParentRoot, StateRoot = Keccak.Compute(BitConverter.GetBytes(i)), Body = real.Body };
            forged.Add((blocks[i], new ForkedSignedBeaconBlock.OfGloas(new SignedBeaconBlockGloas { Message = message, Signature = blocks[i + 1].Signed.Signature }), SszRoots.HashTreeRoot(message)));
        }

        // Children built on an evicted parent's unverified full payload take the deferral path, which regenerates too.
        for (int i = 1; i <= 2; i++)
        {
            SignedGloasChain.Block fullChild = _chain.Next(blocks[i], blocks[i].Signed.Message!.Slot + 1, full: true, (byte)(0xF0 + i));
            BeaconBlockGloas real = fullChild.Signed.Message!;
            BeaconBlockGloas message = new() { Slot = real.Slot, ProposerIndex = real.ProposerIndex, ParentRoot = real.ParentRoot, StateRoot = Keccak.Compute(BitConverter.GetBytes(-i)), Body = real.Body };
            forged.Add((blocks[i], new ForkedSignedBeaconBlock.OfGloas(new SignedBeaconBlockGloas { Message = message, Signature = fullChild.Signed.Signature }), SszRoots.HashTreeRoot(message)));
        }

        bool parentsEvicted = forged.All(f => states.GetGloasBlockState(f.Parent.Root) is null);
        BlockImportResult[] results = [.. forged.Select(f => importer.Import(f.Forged, f.Root, verifySignatures: true))];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(forged.Select(f => f.Parent.Root).Distinct().Count(), Is.GreaterThan(2 * (int)ForkSlot), "fixture: more forged parents than the per-block tier holds");
        Assert.That(parentsEvicted, Is.True, "fixture: every forged child names a parent whose state left every tier");
        Assert.That(results, Is.All.EqualTo(BlockImportResult.Invalid));
        Assert.That(logger.LogList, Has.None.Contains("Regenerated the post-state"));
        Assert.That(blocks.TakeLast(2 * (int)ForkSlot).Select(b => states.GetGloasBlockState(b.Root)), Is.All.Not.Null);
    }

    [Test]
    public void Signed_blocks_on_evicted_parents_regenerate_within_a_per_slot_budget()
    {
        List<SignedGloasChain.Block> blocks = RegenerationLineage(_chain);

        ulong wallSlot = blocks[^1].Signed.Message!.Slot + 2;
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(_chain.Spec.GenesisTime + wallSlot * _chain.Spec.SecondsPerSlot + 1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        Import(importer, [.. blocks]);
        SignedGloasChain.Block[] siblings = [.. Enumerable.Range(1, 3).Select(i => _chain.Next(blocks[i], 3 * ForkSlot + (ulong)i, full: false, (byte)(0xF0 + i)))];

        BeaconBlockGloas repeat = _chain.Next(blocks[5], siblings[0].Signed.Message!.Slot, full: false, 0xF5).Signed.Message!;
        repeat.ProposerIndex = siblings[0].Signed.Message!.ProposerIndex;
        Hash256 repeatRoot = SszRoots.HashTreeRoot(repeat);
        Hash256 domain = siblings[0].PostState.GetDomain(DomainType.BeaconProposer, siblings[0].PostState.GetCurrentEpoch());
        ForkedSignedBeaconBlock repeatSigned = new ForkedSignedBeaconBlock.OfGloas(new SignedBeaconBlockGloas { Message = repeat, Signature = Sign(ValidatorKey((int)repeat.ProposerIndex), Domains.ComputeSigningRoot(repeatRoot, domain)) });

        BlockImportResult Gossip(ForkedSignedBeaconBlock block, Hash256 root) => importer.Import(block, root, verifySignatures: true);
        BlockImportResult[] sameSlot =
        [
            Gossip(siblings[0].Forked, siblings[0].Root),
            Gossip(repeatSigned, repeatRoot),
            Gossip(siblings[1].Forked, siblings[1].Root),
            Gossip(siblings[2].Forked, siblings[2].Root),
        ];
        ImportRefusal spentRefusal = importer.LastRefusal;
        timestamper.Add(TimeSpan.FromSeconds(_chain.Spec.SecondsPerSlot));
        BlockImportResult nextSlot = Gossip(siblings[2].Forked, siblings[2].Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(sameSlot, Is.EqualTo(new[]
        {
            BlockImportResult.Imported,
            BlockImportResult.UnknownParent,
            BlockImportResult.Imported,
            BlockImportResult.UnknownParent,
        }));
        Assert.That(nextSlot, Is.EqualTo(BlockImportResult.Imported));
        Assert.That(spentRefusal, Is.EqualTo(ImportRefusal.RegenerationBudget));
    }

    [Test]
    public void Gossip_block_deferred_after_its_regeneration_regenerates_again_on_retry()
    {
        List<SignedGloasChain.Block> blocks = RegenerationLineage(_chain);

        ulong blockSlot = blocks[^1].Signed.Message!.Slot + 2;
        // Within MAXIMUM_GOSSIP_CLOCK_DISPARITY before the block's slot, so it waits for its slot after its transition.
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(_chain.Spec.GenesisTime + blockSlot * _chain.Spec.SecondsPerSlot).AddMilliseconds(-200));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        Import(importer, [.. blocks]);
        SignedGloasChain.Block early = _chain.Next(blocks[1], blockSlot, full: false, 0xF1);

        BlockImportResult first = importer.Import(early.Forked, early.Root, verifySignatures: true);
        BlockImportResult[] churn = [.. new[] { 2, 3 }.Select(i =>
        {
            SignedGloasChain.Block other = _chain.Next(blocks[i], blockSlot - 4 + (ulong)i, full: false, (byte)(0xF0 + i));
            return importer.ImportRequested(other.Forked, other.Root);
        })];
        bool parentChurned = states.GetGloasBlockState(blocks[1].Root) is null;
        timestamper.Add(TimeSpan.FromSeconds(1));
        BlockImportResult retry = importer.Import(early.Forked, early.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(first, Is.EqualTo(BlockImportResult.FutureSlot), "fixture: the block waits for its slot");
        Assert.That(churn, Is.All.EqualTo(BlockImportResult.Imported), "fixture");
        Assert.That(parentChurned, Is.True, "fixture: the first regenerated state was pushed out");
        Assert.That(retry, Is.EqualTo(BlockImportResult.Imported));
    }

    [Test]
    public void Blocks_fetched_by_root_regenerate_within_their_own_per_slot_budget()
    {
        List<SignedGloasChain.Block> blocks = RegenerationLineage(_chain);

        ulong wallSlot = blocks[^1].Signed.Message!.Slot + 2;
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(_chain.Spec.GenesisTime + wallSlot * _chain.Spec.SecondsPerSlot + 1));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, timestamper));
        Import(importer, [.. blocks]);
        SignedGloasChain.Block[] fetched = [.. Enumerable.Range(1, 5).Select(i => _chain.Next(blocks[i], 3 * ForkSlot + (ulong)i, full: false, (byte)(0xF0 + i)))];
        SignedGloasChain.Block gossip = _chain.Next(blocks[6], 3 * ForkSlot + 6, full: false, 0xF6);

        BlockImportResult[] sameSlot = [.. fetched.Select(f => importer.ImportRequested(f.Forked, f.Root, fetchedByRoot: true))];
        ImportRefusal budgetRefusal = importer.LastRefusal;
        // A proposer with no cached key is refused before any budget, and no later slot changes that.
        BeaconBlockGloas keyless = _chain.Next(blocks[7], 3 * ForkSlot + 7, full: false, 0xF7).Signed.Message!;
        keyless.ProposerIndex = 1UL << 40;
        BlockImportResult keylessResult = importer.ImportRequested(new ForkedSignedBeaconBlock.OfGloas(new SignedBeaconBlockGloas { Message = keyless }), SszRoots.HashTreeRoot(keyless), fetchedByRoot: true);
        ImportRefusal keylessRefusal = importer.LastRefusal;
        BlockImportResult gossipResult = importer.Import(gossip.Forked, gossip.Root, verifySignatures: true);
        timestamper.Add(TimeSpan.FromSeconds(_chain.Spec.SecondsPerSlot));
        BlockImportResult nextSlot = importer.ImportRequested(fetched[2].Forked, fetched[2].Root, fetchedByRoot: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(sameSlot, Is.EqualTo(new[]
        {
            BlockImportResult.Imported,
            BlockImportResult.Imported,
            BlockImportResult.UnknownParent,
            BlockImportResult.UnknownParent,
            BlockImportResult.UnknownParent,
        }));
        Assert.That(budgetRefusal, Is.EqualTo(ImportRefusal.RegenerationBudget));
        Assert.That((keylessResult, keylessRefusal), Is.EqualTo((BlockImportResult.UnknownParent, ImportRefusal.None)));
        Assert.That(gossipResult, Is.EqualTo(BlockImportResult.Imported), "the gossip budget is apart");
        Assert.That(nextSlot, Is.EqualTo(BlockImportResult.Imported));
    }

    [Test]
    public void Gloas_regeneration_crosses_the_fork_and_replays_at_most_one_epoch([Values] bool beyondBound)
    {
        BeaconChainStore store = _chain.CreateStore();
        int count = (int)_chain.Spec.SlotsPerEpoch + (beyondBound ? 1 : 0);
        List<SignedGloasChain.Block> blocks = [];
        SignedGloasChain.Block? tip = null;
        for (int i = 0; i < count; i++)
        {
            tip = _chain.Next(tip, ForkSlot + (ulong)i, full: i % 2 == 1, (byte)(0x40 + i));
            store.PutForkedBlock(tip.Root, tip.Forked);
            blocks.Add(tip);
        }

        Hash256[] ancestry = [.. blocks.Select(static b => b.Root).Reverse(), _chain.AnchorRoot];
        HashSet<Hash256> gloasRoots = [.. blocks.Select(static b => b.Root)];
        PubkeyCache pubkeys = new();
        pubkeys.Build(_chain.AnchorState.Validators!);
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        PostStateCache states = new(store, _chain.Spec, _chain.AnchorRoot, _chain.AnchorState, isGloasBlock: gloasRoots.Contains, logManager: new OneLoggerLogManager(new ILogger(logger)),
            pubkeys: pubkeys, ancestors: root => ancestry.SkipWhile(r => r != root));
        Hash256 anchorStateRoot = SszRoots.HashTreeRoot(_chain.AnchorState);
        Hash256[] live = [.. Enumerable.Range(0, 2 * (int)ForkSlot).Select(static i => Keccak.Compute(BitConverter.GetBytes(i)))];
        foreach (Hash256 root in live)
        {
            states.RetainGloas(root, blocks[0].PostState);
        }

        long started = Stopwatch.GetTimestamp();
        BeaconStateGloas? regenerated = states.GetOrRegenerateGloasBlockState(blocks[^1].Root);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!beyondBound) TestContext.Out.WriteLine($"Gloas regeneration across the fork: {count} blocks in {elapsedMs:F1} ms, {elapsedMs / count:F2} ms per block, {_chain.AnchorState.Validators!.Length} validators");

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(regenerated is null ? null : SszRoots.HashTreeRoot(regenerated), Is.EqualTo(beyondBound ? null : blocks[^1].Signed.Message!.StateRoot));
        Assert.That(states.GetGloasBlockState(blocks[^1].Root), Is.SameAs(regenerated), "a regenerated state is retained, so the next import naming it replays nothing");
        Assert.That(SszRoots.HashTreeRoot(_chain.AnchorState), Is.EqualTo(anchorStateRoot));
        Assert.That(live.Select(states.GetGloasBlockState), Is.All.Not.Null, "regenerated states are kept apart from the states of live blocks");
        Assert.That(logger.LogList, beyondBound ? Has.One.Contains($"no ancestor state is held within {_chain.Spec.SlotsPerEpoch} blocks") : Is.Empty);
    }

    [TestCase(ForkSlot, 1000L, 0L, true)]
    [TestCase(ForkSlot, 2999L, 0L, true)]
    [TestCase(ForkSlot, 3000L, 0L, false)]
    [TestCase(ForkSlot, 5000L, 0L, false)]
    [TestCase(ForkSlot + 1, 1000L, 0L, false)]
    [TestCase(ForkSlot, 2000L, 1000L, true)]
    public void Proposer_boost_follows_the_clock_at_import(ulong clockSlot, long msIntoSlot, long msPerClockRead, bool boosted)
    {
        ForkChoiceSnapshotHolder snapshots = new();
        DateTime arrival = DateTime.UnixEpoch.AddSeconds(_chain.Spec.GenesisTime + clockSlot * _chain.Spec.SecondsPerSlot).AddMilliseconds(msIntoSlot);
        SlotClock clock = new(_chain.Spec, new AdvancingTimestamper(arrival, TimeSpan.FromMilliseconds(msPerClockRead)));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), snapshots: snapshots, clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);

        Import(importer, first);
        importer.ComputeHead();

        Assert.That(snapshots.Current!.ProposerBoostRoot, Is.EqualTo(boosted ? first.Root : Hash256.Zero));
    }

    [Test]
    public void Factory_importer_refuses_an_envelope_whose_blobs_it_cannot_sample()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        PubkeyCache pubkeys = new();
        pubkeys.Build(_chain.AnchorState.Validators!);
        BlockImporterFactory factory = new(_chain.Spec, _chain.CreateStore(), pubkeys, engine, new BeaconChainConfig(), LimboLogs.Instance, new DataColumnSidecarPool(),
            clock: new SlotClock(_chain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(_chain.Spec.GenesisTime + (ForkSlot + 1) * _chain.Spec.SecondsPerSlot))));
        IBlockImporter importer = factory.Create(new ForkedBeaconState.OfFulu(_chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(_chain.AnchorBlock), _chain.AnchorRoot);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1, blobCommitments: [default]);
        importer.Import(first.Forked, first.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(importer.ImportEnvelope(first.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.DataUnavailable));
        Assert.That(engine.EnvelopeCalls, Is.Zero);
    }

    public enum HeldProposal
    {
        Signed,
        BodyAltered,
        OtherProposerSigned,
        ParentNotDeferred,
        PastAncestorLookahead,
        NotAfterParentSlot,
        FromTheFuture,
    }

    [TestCase(HeldProposal.Signed, BlockImportResult.ParentPayloadUnverified)]
    [TestCase(HeldProposal.BodyAltered, BlockImportResult.Invalid)]
    [TestCase(HeldProposal.OtherProposerSigned, BlockImportResult.Invalid)]
    [TestCase(HeldProposal.ParentNotDeferred, BlockImportResult.UnknownParent)]
    [TestCase(HeldProposal.PastAncestorLookahead, BlockImportResult.UnknownParent)]
    [TestCase(HeldProposal.NotAfterParentSlot, BlockImportResult.Invalid)]
    [TestCase(HeldProposal.FromTheFuture, BlockImportResult.Invalid)]
    public async Task Child_of_a_deferred_block_is_deferred_only_when_its_expected_proposer_signed_it(HeldProposal proposal, BlockImportResult expected)
    {
        SlotClock? clock = proposal == HeldProposal.FromTheFuture ? ClockAt(_chain, ForkSlot + 2, millisecondsEarly: GossipRouter.MaximumGossipClockDisparityMs + 1) : null;
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block parked = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block held = _chain.Next(parked, proposal == HeldProposal.PastAncestorLookahead ? 3 * ForkSlot : ForkSlot + 2, full: false, 0xA3);
        Import(importer, first);
        if (proposal != HeldProposal.ParentNotDeferred)
        {
            Assert.That(importer.Import(parked.Forked, parked.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: the parent waits on its own parent's payload");
        }

        BeaconBlockGloas message = held.Signed.Message!;
        switch (proposal)
        {
            case HeldProposal.BodyAltered:
                message.Body!.Graffiti = Hash(0x66);
                break;
            case HeldProposal.OtherProposerSigned:
                // Validly signed by the validator it names, so only the lookahead can tell it is not the expected proposer.
                message.ProposerIndex = (message.ProposerIndex + 1) % ValidatorCount;
                SignAsProposer(held.Signed, first.PostState);
                break;
            case HeldProposal.NotAfterParentSlot:
                // Signed by the expected proposer of the parent's slot, so only the slot check can refuse it.
                message.Slot = parked.Signed.Message!.Slot;
                message.ProposerIndex = parked.Signed.Message.ProposerIndex;
                SignAsProposer(held.Signed, first.PostState);
                break;
        }

        Assert.That(importer.Import(held.Forked, SszRoots.HashTreeRoot(message), verifySignatures: true), Is.EqualTo(expected));
        Assert.That(importer.LastRefusal == ImportRefusal.LocalAdmission, Is.EqualTo(proposal is HeldProposal.FromTheFuture),
            "the on_block checks against this node's store are told apart from a forged proposal and from a slot not after the parent's");
        Assert.That(((IBlockImporter)importer).RejectGossip, Is.False, "a deferred parent is still unknown for gossip validation");
        if (expected == BlockImportResult.Invalid)
            await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, held.Forked,
                Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(held.Signed)), MessageValidity.Ignored);
    }

    [Test]
    public void Child_of_a_deferred_block_found_invalid_is_no_longer_deferred()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block parked = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block child = _chain.Next(parked, ForkSlot + 2, full: false, 0xA3);
        Import(importer, first);
        parked.Signed.Message!.StateRoot = Hash(0x77);
        SignAsProposer(parked.Signed, first.PostState);
        Hash256 parkedRoot = SszRoots.HashTreeRoot(parked.Signed.Message);
        child.Signed.Message!.ParentRoot = parkedRoot;
        SignAsProposer(child.Signed, first.PostState);
        Hash256 childRoot = SszRoots.HashTreeRoot(child.Signed.Message);

        BlockImportResult parkedBeforeEnvelope = importer.Import(parked.Forked, parkedRoot, verifySignatures: true);
        BlockImportResult childBeforeEnvelope = importer.Import(child.Forked, childRoot, verifySignatures: true);
        ExecutionPayloadEnvelopeImportResult envelope = importer.ImportEnvelope(first.Envelope);
        BlockImportResult parkedAfterEnvelope = importer.Import(parked.Forked, parkedRoot, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(parkedBeforeEnvelope, Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: only the proposer and signature are checked before the envelope");
        Assert.That(childBeforeEnvelope, Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: the child is deferred behind it");
        Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
        Assert.That(parkedAfterEnvelope, Is.EqualTo(BlockImportResult.Invalid), "fixture: the state root does not match");
        Assert.That(importer.Import(child.Forked, childRoot, verifySignatures: true), Is.EqualTo(BlockImportResult.UnknownParent));
    }

    [Test]
    public void Deferred_blocks_are_bounded_and_forgotten_once_finalized()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block parked = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        SignedGloasChain.Block finalized = _chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        Import(importer, first, finalized);

        const int maxDeferredBlocks = 256;
        BeaconBlockGloas message = parked.Signed.Message!;
        for (int i = 0; i <= maxDeferredBlocks; i++)
        {
            message.Body!.Graffiti = Keccak.Compute(BitConverter.GetBytes(i));
            SignAsProposer(parked.Signed, first.PostState);
            Assert.That(importer.Import(parked.Forked, SszRoots.HashTreeRoot(message), verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: each equivocation is signed by the expected proposer");
        }

        int deferredWhenFull = DeferredCount(importer);
        Finalize(importer, new CheckpointRef(2, finalized.Root));
        importer.OnFinalized(new CheckpointRef(2, finalized.Root));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(deferredWhenFull, Is.EqualTo(maxDeferredBlocks));
        Assert.That(DeferredCount(importer), Is.Zero);
    }

    [Test]
    public void Deferred_block_at_or_below_the_finalized_epoch_start_is_forgotten_once_finalized([Values(ForkSlot + 2, ForkSlot + 8)] ulong siblingSlot)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block finalized = _chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        SignedGloasChain.Block sibling = _chain.Next(first, siblingSlot, full: true, 0xA4);
        Import(importer, first, finalized);
        Assert.That(importer.Import(sibling.Forked, sibling.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture");

        Finalize(importer, new CheckpointRef(2, finalized.Root));
        importer.OnFinalized(new CheckpointRef(2, finalized.Root));

        Assert.That(DeferredCount(importer), Is.Zero, "on_block refuses a block at or below the finalized epoch's start slot, so it must not keep a deferral place");
    }

    [Test]
    public void Release_forgets_a_deferred_block_at_once()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block parked = _chain.Next(first, ForkSlot + 1, full: true, 0xA2);
        Import(importer, first);
        Assert.That(importer.Import(parked.Forked, parked.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture");
        int before = DeferredCount(importer);

        importer.Release(parked.Root);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(before, Is.EqualTo(1));
        Assert.That(DeferredCount(importer), Is.Zero);
    }

    internal static int DeferredCount(BlockImporter importer) =>
        ((ICollection)typeof(BlockImporter).GetField("_deferred", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!).Count;

    private static void SignAsProposer(SignedBeaconBlockGloas block, BeaconStateGloas state)
    {
        Hash256 proposerDomain = state.GetDomain(DomainType.BeaconProposer, state.GetCurrentEpoch());
        block.Signature = Sign(ValidatorKey((int)block.Message!.ProposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block.Message), proposerDomain));
    }

    public enum Forgery
    {
        None,
        BodyAltered,
        OtherProposer,
        OtherProposerSigned,
        ProposerPastRegistry,
    }

    [Test]
    public async Task Gossip_block_rejects_invalid_proposals_and_charges_the_delivering_peer([Values] Forgery forgery)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block parent = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(parent, ForkSlot + 1, full: false, 0xA2);
        Import(importer, parent);
        MutateProposal(child.Signed, parent.PostState, forgery);
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, child.Forked,
            Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(child.Signed)), forgery == Forgery.None ? MessageValidity.Accepted : MessageValidity.Rejected);
    }

    [Test]
    public async Task Gossip_wrong_proposer_obeys_parent_payload_validation_order([Values] bool payloadVerified, [Values] bool validSignature)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: ClockAt(_chain, ForkSlot + 1, millisecondsEarly: 0));
        SignedGloasChain.Block parent = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(parent, ForkSlot + 1, full: true, 0xA2);
        Import(importer, parent);
        if (payloadVerified)
        {
            Assert.That(importer.ImportEnvelope(parent.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
        }

        MutateProposal(child.Signed, parent.PostState, validSignature ? Forgery.OtherProposerSigned : Forgery.OtherProposer);
        PubkeyCache pubkeys = new();
        pubkeys.Build(parent.PostState.Validators!);
        Assert.That(GloasBlockProcessing.VerifyProposerSignature(parent.PostState, child.Signed, pubkeys), Is.EqualTo(validSignature));
        MessageValidity expected = validSignature && !payloadVerified ? MessageValidity.Ignored : MessageValidity.Rejected;
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, child.Forked,
            Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(child.Signed)), expected);
    }

    public enum GossipBlockFault
    {
        ParentSlot,
        FinalizedAncestry,
        BlobCount,
        BidParentRoot,
        BidExecutionHead,
    }

    [Test]
    public async Task Gossip_head_registry_precedes_unverified_parent_payload([Values] bool validSignature, [Values] bool keysAvailable)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: ClockAt(_chain, ForkSlot + 2, millisecondsEarly: 0));
        SignedGloasChain.Block parent = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block head = _chain.Next(parent, ForkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block child = _chain.Next(parent, ForkSlot + 2, full: true, 0xA3);
        Import(importer, parent, head);
        ForkChoiceRunner runner = (ForkChoiceRunner)typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        PubkeyCache pubkeys = (PubkeyCache)typeof(BlockImporter).GetField("_pubkeys", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        Assert.That(runner.GetHead(), Is.EqualTo(head.Root));
        BeaconStateGloas headState = states.GetGloasBlockState(head.Root)!.Clone();
        ulong index = (ulong)headState.Validators!.Length;
        headState.Validators = [.. headState.Validators, new Validator
        {
            Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey((int)index)).Compress()),
            ActivationEpoch = Presets.FarFutureEpoch,
            ExitEpoch = Presets.FarFutureEpoch,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        }];
        states.RetainGloas(head.Root, headState);
        pubkeys.Build(headState.Validators);
        child.Signed.Message!.ProposerIndex = index;
        SignAsProposer(child.Signed, headState);
        if (!validSignature) child.Signed.Signature = default;
        Assert.That(GloasBlockProcessing.VerifyProposerSignature(headState, child.Signed, pubkeys), Is.EqualTo(validSignature));
        if (!keysAvailable) pubkeys.Build(parent.PostState.Validators!);

        Assert.That(importer.Import(child.Forked, child.Forked.ComputeMessageRoot(), verifySignatures: true), Is.EqualTo(BlockImportResult.Invalid),
            "head authentication changes the gossip verdict, not the parent's proposal admission result");
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, child.Forked,
            Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(child.Signed)),
            keysAvailable && !validSignature ? MessageValidity.Rejected : MessageValidity.Ignored);
    }

    [Test]
    public async Task Gossip_block_refusal_obeys_parent_payload_validation_order(
        [Values] GossipBlockFault fault, [Values] bool payloadVerified, [Values] bool validSignature)
    {
        BeaconChainStore store = _chain.CreateStore();
        SlotClock clock = ClockAt(_chain, 2 * ForkSlot + 2, millisecondsEarly: 0);
        ForkChoiceSnapshotHolder snapshots = new();
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), snapshots: snapshots, store: store, clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block checkpoint = _chain.Next(first, 2 * ForkSlot, full: false, 0xA2);
        SignedGloasChain.Block parent = _chain.Next(first, 2 * ForkSlot + 1, full: false, 0xA3);
        SignedGloasChain.Block child = _chain.Next(parent, 2 * ForkSlot + 2, full: true, 0xA4);
        Import(importer, first, checkpoint, parent);
        if (payloadVerified)
            Assert.That(importer.ImportEnvelope(parent.Envelope), Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));

        BeaconBlockGloas message = child.Signed.Message!;
        switch (fault)
        {
            case GossipBlockFault.ParentSlot:
                message.Slot = parent.Signed.Message!.Slot;
                break;
            case GossipBlockFault.FinalizedAncestry:
                Finalize(importer, new CheckpointRef(2, checkpoint.Root));
                break;
            case GossipBlockFault.BlobCount:
                int count = (int)(_chain.Spec.GetBlobParameters(_chain.Spec.GetEpoch(message.Slot))?.MaxBlobsPerBlock ?? _chain.Spec.MaxBlobsPerBlockElectra) + 1;
                message.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = Enumerable.Range(0, count)
                    .Select(_ => SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength])).ToArray();
                break;
            case GossipBlockFault.BidParentRoot:
                message.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockRoot = Hash(0xCC);
                break;
            case GossipBlockFault.BidExecutionHead:
                message.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockHash = Hash(0xCC);
                break;
        }

        SignAsProposer(child.Signed, parent.PostState);
        if (!validSignature) child.Signed.Signature = default;
        PubkeyCache pubkeys = new();
        pubkeys.Build(parent.PostState.Validators!);
        Assert.That(GloasBlockProcessing.VerifyProposerSignature(parent.PostState, child.Signed, pubkeys), Is.EqualTo(validSignature));
        Assert.That(ImportOrFailIfStuck(importer, child.Forked, child.Forked.ComputeMessageRoot()), Is.EqualTo(BlockImportResult.Invalid),
            "invalid children must still be refused before a transition or a payload retry is queued");
        Assert.That(DeferredCount(importer), Is.Zero);
        importer.ComputeHead();
        ColumnGossipRouter headers = new(_chain.Spec, clock, LimboLogs.Instance, forkChoice: snapshots);
        GossipRouter router = new(_chain.Spec, clock, LimboLogs.Instance, store, headers: headers);
        await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, child.Forked,
            Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(child.Signed)),
            validSignature && !payloadVerified && fault != GossipBlockFault.BidExecutionHead ? MessageValidity.Ignored : MessageValidity.Rejected, router);
    }

    [TestCase(Forgery.None, BlockImportResult.ParentPayloadUnverified, false)]
    [TestCase(Forgery.BodyAltered, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.OtherProposer, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.OtherProposerSigned, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.ProposerPastRegistry, BlockImportResult.Invalid, false)]
    [TestCase(Forgery.None, BlockImportResult.ParentPayloadUnverified, true)]
    [TestCase(Forgery.BodyAltered, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.OtherProposer, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.OtherProposerSigned, BlockImportResult.Invalid, true)]
    [TestCase(Forgery.ProposerPastRegistry, BlockImportResult.Invalid, true)]
    public void Full_child_is_deferred_only_when_its_expected_proposer_signed_it(Forgery forgery, BlockImportResult expected, bool pastLookahead)
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(first, pastLookahead ? 3 * ForkSlot : ForkSlot + 1, full: true, 0xA2);
        Import(importer, first);
        BeaconBlockGloas message = child.Signed.Message!;
        MutateProposal(child.Signed, first.PostState, forgery);

        BlockImportResult result = importer.Import(child.Forked, SszRoots.HashTreeRoot(message), verifySignatures: true);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(expected));
        Assert.That(((IBlockImporter)importer).RejectGossip, Is.EqualTo(forgery is not (Forgery.None or Forgery.OtherProposerSigned)),
            "an unverified parent payload precedes expected-proposer rejection, but follows signature rejection");
    }

    private static void MutateProposal(SignedBeaconBlockGloas block, BeaconStateGloas state, Forgery forgery)
    {
        BeaconBlockGloas message = block.Message!;
        switch (forgery)
        {
            case Forgery.BodyAltered:
                message.Body!.Graffiti = Hash(0x66);
                break;
            case Forgery.OtherProposer:
            case Forgery.OtherProposerSigned:
                message.ProposerIndex = (message.ProposerIndex + 1) % ValidatorCount;
                if (forgery == Forgery.OtherProposerSigned) SignAsProposer(block, state);
                break;
            case Forgery.ProposerPastRegistry:
                message.ProposerIndex = ulong.MaxValue;
                break;
        }
    }

    public enum OnBlockAssertion
    {
        CurrentSlot,
        FarFutureSlot,
        AfterFinalizedSlot,
        DescendsFromFinalized,
        AfterParentSlot,
    }

    [Test]
    public async Task Block_failing_an_on_block_assertion_is_refused_before_its_state_transition([Values] OnBlockAssertion assertion)
    {
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        SlotClock? clock = assertion is OnBlockAssertion.CurrentSlot ? ClockAt(_chain, ForkSlot + 1, millisecondsEarly: GossipRouter.MaximumGossipClockDisparityMs + 1) : null;
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)), clock: clock);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);
        SignedGloasChain.Block block;
        switch (assertion)
        {
            case OnBlockAssertion.CurrentSlot:
                block = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                break;
            case OnBlockAssertion.FarFutureSlot:
                block = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                block.Signed.Message!.Slot = 1UL << 40;
                break;
            case OnBlockAssertion.AfterParentSlot:
                block = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                block.Signed.Message!.Slot = ForkSlot;
                block.Signed.Message.ProposerIndex = first.Signed.Message!.ProposerIndex;
                SignAsProposer(block.Signed, first.PostState);
                break;
            case OnBlockAssertion.AfterFinalizedSlot:
                // The checkpoint block is at slot 33 and the child sits on the bound, slot 64; full, so a missed bound would park it on the unverified payload.
                SignedGloasChain.Block finalized = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                Import(importer, finalized);
                Finalize(importer, new CheckpointRef(2, finalized.Root));
                block = _chain.Next(finalized, 2 * ForkSlot, full: true, 0xA5);
                break;
            default:
                SignedGloasChain.Block finalizedBranch = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
                SignedGloasChain.Block otherBranch = _chain.Next(first, ForkSlot + 2, full: false, 0xA3);
                SignedGloasChain.Block checkpoint = _chain.Next(finalizedBranch, 2 * ForkSlot, full: false, 0xA4);
                Import(importer, finalizedBranch, otherBranch, checkpoint);
                Finalize(importer, new CheckpointRef(2, checkpoint.Root));
                block = _chain.Next(otherBranch, 2 * ForkSlot + 1, full: false, 0xA5);
                break;
        }

        Hash256 root = SszRoots.HashTreeRoot(block.Signed.Message!);
        logger.LogList.Clear();

        BlockImportResult result = ImportOrFailIfStuck(importer, block.Forked, root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
            Assert.That(importer.LastRefusal, Is.EqualTo(assertion == OnBlockAssertion.AfterParentSlot ? ImportRefusal.None : ImportRefusal.LocalAdmission),
                "a refusal by this node's own store says nothing of the block's data, while a slot not after the parent's is invalid data");
            Assert.That(logger.LogList, Has.One.Contains("before its state transition"));
            Assert.That(importer.IsKnown(root), Is.False);
        }
        if (assertion is OnBlockAssertion.AfterParentSlot or OnBlockAssertion.DescendsFromFinalized)
        {
            await BeaconSyncOrchestratorTests.AssertBlockVerdictAsync(importer, block.Forked,
                Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(block.Signed)), MessageValidity.Rejected);
        }
    }

    [Test]
    public void Block_before_its_slot_starts_waits_for_the_slot()
    {
        DateTime slotStart = TickFinalityFixture.SlotStart(_chain.Spec, ForkSlot + 1);
        ManualTimestamper time = new(slotStart.AddMilliseconds(-GossipRouter.MaximumGossipClockDisparityMs));
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), clock: new SlotClock(_chain.Spec, time));
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);
        SignedGloasChain.Block block = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);

        BlockImportResult early = importer.Import(block.Forked, block.Root, verifySignatures: true);
        bool knownEarly = importer.IsKnown(block.Root);
        ulong member = first.PostState.GetPtc(ForkSlot, _chain.Spec).Indices![0];
        importer.ComputeHead();
        bool? voteAccepted = importer.OnGossipPayloadAttestation(PtcVote(first, member, payloadPresent: true));
        time.Set(slotStart);
        BlockImportResult onTime = importer.Import(block.Forked, block.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(early, Is.EqualTo(BlockImportResult.FutureSlot));
        Assert.That(knownEarly, Is.False);
        Assert.That(voteAccepted, Is.True, "the previous slot's signed PTC vote still counts before the next slot starts");
        Assert.That(onTime, Is.EqualTo(BlockImportResult.Imported));
    }

    /// <summary>The timeout guards against unbounded process_slots; the caller still asserts the specific refusal.</summary>
    internal static BlockImportResult ImportOrFailIfStuck(IBlockImporter importer, ForkedSignedBeaconBlock block, Hash256 root)
    {
        Task<BlockImportResult> import = Task.Run(() => importer.Import(block, root, verifySignatures: true));
        Assert.That(import.Wait(TimeSpan.FromMinutes(1)), Is.True, "the import is still running the state transition toward the block's slot");
        return import.Result;
    }

    private static SlotClock ClockAt(SignedGloasChain chain, ulong slot, long millisecondsEarly) =>
        new(chain.Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeMilliseconds((long)(chain.Spec.GenesisTime + slot * chain.Spec.SecondsPerSlot) * 1000 - millisecondsEarly).UtcDateTime));

    /// <summary>Inject finalization because a short fixture cannot supply epochs of justification votes.</summary>
    private static void Finalize(BlockImporter importer, CheckpointRef finalized)
    {
        object runner = typeof(BlockImporter).GetField("_runner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        ForkChoiceStore store = (ForkChoiceStore)typeof(ForkChoiceRunner).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
        store.UpdateCheckpoints(finalized, finalized);
    }

    [Test]
    public void Block_refused_by_fork_choice_leaves_no_state_behind()
    {
        BeaconChainStore store = _chain.CreateStore();
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), store: store);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block child = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first);
        importer.OnInvalidExecutionPayload(first.Root, latestValidHash: null);

        BlockImportResult result = importer.Import(child.Forked, child.Root, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "fixture: fork choice refuses a child of an invalid payload");
        Assert.That(store.HasBlock(child.Root), Is.False, "the refused block is not stored");
        Assert.That(importer.IsKnown(child.Root), Is.False);
    }

    [Test]
    public void Envelope_for_a_block_fork_choice_no_longer_holds_never_reaches_the_engine()
    {
        SignedGloasChain.EnvelopeEngine engine = new();
        BlockImporter importer = _chain.CreateImporter(engine);
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block pruned = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        Import(importer, first);
        // Stands in for finalization pruning: that needs a finalized block past the proto-array's 256-node prune threshold.
        PostStateCache states = (PostStateCache)typeof(BlockImporter).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(importer)!;
        states.RetainGloas(pruned.Root, pruned.PostState);

        ExecutionPayloadEnvelopeImportResult result = importer.ImportEnvelope(pruned.Envelope);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
        Assert.That(engine.EnvelopeCalls, Is.Zero);
    }

    [Test]
    public void Block_on_a_sibling_branch_across_an_epoch_boundary_imports()
    {
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine());
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        SignedGloasChain.Block left = _chain.Next(first, ForkSlot + 1, full: false, 0xA2);
        SignedGloasChain.Block right = _chain.Next(first, ForkSlot + 2, full: false, 0xA3);
        SignedGloasChain.Block leftNextEpoch = _chain.Next(left, 2 * ForkSlot, full: false, 0xA4);
        SignedGloasChain.Block rightNextEpoch = _chain.Next(right, 2 * ForkSlot + 1, full: false, 0xA5);
        SignedGloasChain.Block leftTip = _chain.Next(leftNextEpoch, 2 * ForkSlot + 2, full: false, 0xA6);
        Import(importer, first, left, right, leftNextEpoch, rightNextEpoch);

        Assert.That(importer.Import(leftTip.Forked, leftTip.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported));
    }

    [Test]
    public void Gloas_head_step_is_not_taken_for_a_reorg()
    {
        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(), logManager: new OneLoggerLogManager(new ILogger(logger)));
        SignedGloasChain.Block first = _chain.Next(null, ForkSlot, full: false, 0xA1);
        Import(importer, first);

        HeadView head = importer.ComputeHead();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(head.HeadRoot, Is.EqualTo(first.Root), "fixture: the Gloas block is the head");
        Assert.That(logger.LogList, Is.Empty);
    }

    [Test]
    public void Body_attester_slashing_refused_by_fork_choice_is_tolerated_and_counted()
    {
        (BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot) = BlockImporterTests.AnchorWithQueuedValidator(_chain.AnchorState, _chain.AnchorBlock);
        BeaconStateFulu crossing = anchorState.Clone();
        SlotProcessing.ProcessSlots(crossing, ForkSlot, new EpochCache());
        BeaconStateGloas state = GloasForkTransition.UpgradeToGloas(crossing, _chain.Spec);
        ulong onboarded = (ulong)anchorState.Validators!.Length;
        Assert.That(state.Validators, Has.Length.EqualTo(anchorState.Validators.Length + 1), "fixture bug: the queued validator must be onboarded before the slashing block");
        AttesterSlashingGloas slashing = new()
        {
            Attestation1 = new IndexedAttestationGloas { AttestingIndices = [1, onboarded], Data = Vote(ForkSlot, 0, ForkCrossingChain.ForkEpoch, 0x31) },
            Attestation2 = new IndexedAttestationGloas { AttestingIndices = [1, onboarded], Data = Vote(ForkSlot, 0, ForkCrossingChain.ForkEpoch, 0x41) },
        };
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0xA1)));
        block.Message!.Body!.AttesterSlashings = [slashing];
        ApplyBlock(state, block, new EpochCache());
        block.Message.StateRoot = SszRoots.HashTreeRoot(state);
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        Assert.That(state.Validators![1].Slashed, Is.True, "fixture bug: the transition must accept the slashing");
        BlockImporter importer = _chain.CreateImporter(new SignedGloasChain.EnvelopeEngine(),
            fuluAnchor: new SignedGloasChain.FuluBlock(anchorBlock, anchorRoot, anchorState));
        long refusedBefore = RefusedByForkChoice("body_attester_slashing");

        BlockImportResult result = importer.Import(new ForkedSignedBeaconBlock.OfGloas(block), root, verifySignatures: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the transition accepted the slashing, so fork choice's refusal must not sink the block");
        Assert.That(RefusedByForkChoice("body_attester_slashing") - refusedBefore, Is.EqualTo(1), "a tolerated refusal must still be observable");
    }

    private static long RefusedByForkChoice(string operation) =>
        Metrics.BeaconChainForkChoiceRejections.GetValueOrDefault(new StringLabel(operation));

    private static List<SignedGloasChain.Block> RegenerationLineage(SignedGloasChain chain)
    {
        List<SignedGloasChain.Block> blocks = [];
        SignedGloasChain.Block? tip = null;
        for (ulong slot = ForkSlot; slot < 3 * ForkSlot + 8; slot++)
        {
            tip = chain.Next(tip, slot, full: false, (byte)slot);
            blocks.Add(tip);
        }
        return blocks;
    }

    private static void Import(BlockImporter importer, params SignedGloasChain.Block[] blocks)
    {
        foreach (SignedGloasChain.Block block in blocks)
        {
            Assert.That(importer.Import(block.Forked, block.Root, verifySignatures: true), Is.EqualTo(BlockImportResult.Imported), $"fixture: block at slot {block.Signed.Message!.Slot}");
        }
    }

    private sealed class AdvancingTimestamper(DateTime start, TimeSpan step) : ITimestamper
    {
        private DateTime _now = start;

        public DateTime UtcNow
        {
            get
            {
                DateTime now = _now;
                _now += step;
                return now;
            }
        }
    }
}
