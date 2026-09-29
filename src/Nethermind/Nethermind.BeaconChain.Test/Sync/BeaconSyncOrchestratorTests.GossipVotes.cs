// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The gossip work the router hands to fork choice: seen sets it cannot fill itself for slashings, since only the importer verifies
/// signatures, the bounds on the verifies that votes cost, and the order votes and slot ticks reach fork choice in.
/// </summary>
public partial class BeaconSyncOrchestratorTests
{
    /// <summary>
    /// phase0/p2p-interface.md <c>attester_slashing</c>: IGNORE a slashing once every index in the intersection of its two
    /// attestations is in the seen set. Only the intersection is marked, so a slashing over another common index is still new.
    /// </summary>
    [Test]
    public async Task Attester_slashing_indices_are_marked_seen_only_after_fork_choice_accepts_the_slashing([Values] bool accepted, [Values] bool gloas)
    {
        Harness harness = CreateHarness();
        harness.Importer.AcceptsGossipOperations = accepted;
        harness.Orchestrator.RouteGossipEvents();

        MessageValidity first = Slashing(harness, [1, 2], [2, 3], secondSource: 2);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        int consumedBefore = harness.Importer.GossipOperations.Count;
        MessageValidity sameIndex = Slashing(harness, [2, 4], [2, 5], secondSource: 3);
        MessageValidity otherIndex = Slashing(harness, [1, 4], [1, 5], secondSource: 3);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, sameIndex, otherIndex), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Ignored, MessageValidity.Ignored)));
            Assert.That(consumedBefore, Is.EqualTo(1), "fixture: the first slashing reached the importer");
            Assert.That(harness.Router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(accepted ? 1 : 0), "only an accepted slashing marks its intersecting index");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(accepted ? 2 : 3), "a slashing over an index outside the intersection is still new");
        }

        MessageValidity Slashing(Harness h, ulong[] indices1, ulong[] indices2, ulong secondSource) =>
            h.Router.Handle(GossipTopics.AttesterSlashing, gloas, gloas
                ? GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.GloasSlashing(indices1, indices2, secondSource, secondTarget: 4))
                : GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing(indices1, indices2, secondSource, secondTarget: 4)));
    }

    /// <summary>
    /// gloas/p2p-interface.md <c>payload_attestation_message</c> IGNOREs a repeat only after a valid vote, but the router cannot check the
    /// signature and each vote under a PTC member's index costs fork choice a BLS verify. A (slot, validator) pair is therefore
    /// verified once whatever the signature, so a flood of forged repeats costs one verify, and other validators' votes are unaffected.
    /// </summary>
    [Test]
    public async Task Payload_attestation_pair_is_verified_once_whatever_the_signature([Values] bool firstAccepted)
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Importer.AcceptsGossipOperations = firstAccepted;
        harness.Orchestrator.RouteGossipEvents();

        const int forgeries = 3 * BeaconSyncOrchestrator.VotesPerPass;
        MessageValidity[] repeats = new MessageValidity[forgeries];
        MessageValidity first = PtcVote(router, slot, validatorIndex: 7, signatureSeed: 0);
        for (int i = 0; i < forgeries; i++)
        {
            repeats[i] = PtcVote(router, slot, validatorIndex: 7, signatureSeed: (byte)(i + 1));
        }

        MessageValidity otherValidator = PtcVote(router, slot, validatorIndex: 8, signatureSeed: 0);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeats.Append(first).Append(otherValidator), Is.All.EqualTo(MessageValidity.Ignored), "consumed by the router, never forwarded");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(forgeries), "each repeat is dropped before it is queued");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(2), "one verify for the pair and one for the other validator");
            Assert.That(harness.Importer.GossipOperations, Is.All.TypeOf<PayloadAttestationMessage>());
        }
    }

    public enum FloodKind
    {
        PayloadAttestation,
        AttesterSlashing,
        Aggregate,
    }

    /// <summary>
    /// A forged vote under a PTC member's index, a forged aggregate or a forged slashing costs a BLS verify and is never penalized,
    /// so a flood of them is cheap to send: it must neither drop a gossip block nor hold one behind more than one batch of votes.
    /// </summary>
    [Test]
    public async Task Gossip_vote_flood_does_not_drop_or_starve_a_gossip_block([Values] FloodKind kind)
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Importer.AcceptsGossipOperations = false;
        harness.Orchestrator.RouteGossipEvents();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1);
        harness.Importer.Known.Add(anchorRoot);

        const int flood = BeaconSyncOrchestrator.WorkQueueCapacity + 1;
        for (ulong validator = 0; validator < flood; validator++)
        {
            MessageValidity validity = kind switch
            {
                FloodKind.AttesterSlashing => router.Handle(GossipTopics.AttesterSlashing, gloasTopic: false,
                    GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.FuluSlashing([validator], [validator], secondSource: 2, secondTarget: 4))),
                FloodKind.Aggregate => router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(Aggregate(validator))),
                _ => PtcVote(router, slot, validator, payloadPresent: true),
            };
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored), "fixture: each vote is raised");
        }

        bool blockQueued = harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        int votesInFirstPass = harness.Importer.GossipOperations.Count;
        bool importedInFirstPass = harness.Importer.Imports.Exists(static i => i.Slot == AnchorSlot + 1);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task worker = harness.Orchestrator.RunWorkerAsync(cts.Token);
        while (harness.Importer.GossipOperations.Count < flood && !cts.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        int votesVerified = harness.Importer.GossipOperations.Count;
        await cts.CancelAsync();
        Assert.That(async () => await worker, Throws.InstanceOf<OperationCanceledException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockQueued, Is.True, "the votes must not fill the queue gossip blocks wait in");
            Assert.That(importedInFirstPass, Is.True, "the block is imported in the first pass");
            Assert.That(votesInFirstPass, Is.EqualTo(BeaconSyncOrchestrator.VotesPerPass), "one pass verifies one batch of votes");
            Assert.That(votesVerified, Is.EqualTo(flood), "the worker wakes for the remaining votes with no other work queued");
        }

        SignedAggregateAndProofGloas Aggregate(ulong aggregator)
        {
            SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(slot);
            aggregate.Message!.AggregatorIndex = aggregator;
            return aggregate;
        }
    }

    /// <summary>
    /// gloas/fork-choice.md <c>on_payload_attestation_message</c> checks a gossip vote against the store's current slot, so a vote
    /// queued before a slot tick must reach fork choice before that tick moves the store to the next slot.
    /// </summary>
    [Test]
    public async Task Payload_attestation_queued_before_a_slot_tick_reaches_fork_choice_first()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        int ticksBefore = harness.Importer.Ticks.Count;

        Assert.That(PtcVote(router, slot, validatorIndex: 7, payloadPresent: true), Is.EqualTo(MessageValidity.Ignored), "fixture: the vote is raised");
        await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Ticks, Has.Count.EqualTo(ticksBefore + 1), "fixture: the tick was processed");
            Assert.That(harness.Importer.TicksAtGossipOperations, Is.EqualTo((int[])[ticksBefore]), "the vote reached fork choice before the tick");
        }
    }

    /// <summary>A vote queued while the worker drains other work must still wake it, though its wake is read in the same drain.</summary>
    [Test]
    public async Task Payload_attestation_arriving_during_a_pass_wakes_the_worker()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        harness.Importer.OnTick = _ => PtcVote(router, slot, validatorIndex: 7, payloadPresent: true);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task worker = harness.Orchestrator.RunWorkerAsync(cts.Token);
        await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None);
        while (harness.Importer.GossipOperations.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10);
        }

        int votesVerified = harness.Importer.GossipOperations.Count;
        await cts.CancelAsync();
        Assert.That(async () => await worker, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(votesVerified, Is.EqualTo(1), "the vote queued during the pass is verified without other traffic");
    }

    /// <summary>A vote the full vote channel refuses is counted as a dropped gossip message, and the channel stays bounded.</summary>
    [Test]
    public async Task Gossip_votes_past_the_vote_channel_capacity_are_counted_as_dropped()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Importer.AcceptsGossipOperations = false;
        harness.Orchestrator.RouteGossipEvents();
        const int overflow = 10;

        ulong droppedBefore = Metrics.BeaconChainGossipDropped;
        for (ulong validator = 0; validator < BeaconSyncOrchestrator.VoteQueueCapacity + overflow; validator++)
        {
            PtcVote(router, slot, validator);
        }

        ulong dropped = Metrics.BeaconChainGossipDropped - droppedBefore;
        for (int pass = 0; pass < BeaconSyncOrchestrator.VoteQueueCapacity / BeaconSyncOrchestrator.VotesPerPass + 2; pass++)
        {
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped, Is.EqualTo((ulong)overflow));
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(BeaconSyncOrchestrator.VoteQueueCapacity));
        }
    }

    private static MessageValidity PtcVote(GossipRouter router, ulong slot, ulong validatorIndex, bool payloadPresent = true, byte signatureSeed = 0) =>
        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(new PayloadAttestationMessage
        {
            ValidatorIndex = validatorIndex,
            Data = new PayloadAttestationData { BeaconBlockRoot = Keccak.Compute("voted block"), Slot = slot, PayloadPresent = payloadPresent },
            Signature = new BlsSignature([.. Enumerable.Repeat(signatureSeed, BlsSignature.Length)]),
        })));
}
