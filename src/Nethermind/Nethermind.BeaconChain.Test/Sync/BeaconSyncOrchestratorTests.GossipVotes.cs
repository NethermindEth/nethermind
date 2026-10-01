// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
    /// verified at most the attempt limit of times while none verifies, so a flood of forged repeats has a bounded cost, and other
    /// validators' votes are unaffected.
    /// </summary>
    [Test]
    public async Task Payload_attestation_pair_is_verified_at_most_the_attempt_limit_of_times_whatever_the_signature()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Importer.AcceptsGossipOperations = false;
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
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(forgeries + 1 - GossipRouter.PayloadAttestationVerifyAttempts), "each repeat past the limit is dropped before it is queued");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(GossipRouter.PayloadAttestationVerifyAttempts + 1), "the limit for the pair and one for the other validator");
            Assert.That(harness.Importer.GossipOperations, Is.All.TypeOf<PayloadAttestationMessage>());
        }
    }

    /// <summary>
    /// A forged vote that fork choice refuses leaves the pair open, so the member's genuine vote behind it still reaches fork choice; once
    /// a vote is accepted the pair is closed and later votes for it are dropped before the queue.
    /// </summary>
    [Test]
    public async Task Payload_attestation_genuine_vote_after_a_refused_forgery_is_verified_and_closes_the_pair()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();

        harness.Importer.AcceptsGossipOperations = false;
        PtcVote(router, slot, validatorIndex: 7, signatureSeed: 1);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        harness.Importer.AcceptsGossipOperations = true;
        PtcVote(router, slot, validatorIndex: 7, signatureSeed: 2);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        int verifiedBeforeRepeat = harness.Importer.GossipOperations.Count;
        PtcVote(router, slot, validatorIndex: 7, signatureSeed: 3);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifiedBeforeRepeat, Is.EqualTo(2), "the genuine vote reached fork choice behind the forgery");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(2), "a vote after the accepted one is not verified");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    /// <summary>gloas/p2p-interface.md IGNOREs every vote after the first valid one: votes queued before that one is processed must not reach fork choice again.</summary>
    [Test]
    public async Task Payload_attestation_votes_queued_before_the_first_verifies_are_applied_once()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();

        PtcVote(router, slot, validatorIndex: 7, payloadPresent: true, signatureSeed: 1);
        PtcVote(router, slot, validatorIndex: 7, payloadPresent: false, signatureSeed: 2);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A vote the full vote channel refuses must not use up its pair's attempts or its message id: while the channel is full every
    /// copy is refused, and once it drains a later copy, identical or not, is still verified.
    /// </summary>
    [Test]
    public async Task Payload_attestation_refused_by_a_full_vote_channel_can_still_be_verified_later([Values] bool identicalCopy)
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        const ulong member = BeaconSyncOrchestrator.VoteQueueCapacity;
        for (ulong validator = 0; validator < BeaconSyncOrchestrator.VoteQueueCapacity; validator++)
        {
            PtcVote(router, slot, validator);
        }

        ulong droppedBefore = Metrics.BeaconChainGossipDropped;
        const int refusedCopies = GossipRouter.PayloadAttestationVerifyAttempts + 1;
        for (byte seed = 1; seed <= refusedCopies; seed++)
        {
            PtcVote(router, slot, member, signatureSeed: seed);
        }

        ulong dropped = Metrics.BeaconChainGossipDropped - droppedBefore;
        for (int pass = 0; pass < BeaconSyncOrchestrator.VoteQueueCapacity / BeaconSyncOrchestrator.VotesPerPass + 1; pass++)
        {
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }

        int verifiedBefore = harness.Importer.GossipOperations.Count;
        PtcVote(router, slot, member, signatureSeed: identicalCopy ? (byte)1 : (byte)200);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped, Is.EqualTo((ulong)refusedCopies), "fixture: every copy reached the full channel and was refused");
            Assert.That(verifiedBefore, Is.EqualTo(BeaconSyncOrchestrator.VoteQueueCapacity), "fixture: the refused copies were never queued");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(BeaconSyncOrchestrator.VoteQueueCapacity + 1), "the later copy is verified");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.Zero);
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

    // p2p-interface.md beacon_aggregate_and_proof: only accepted aggregates mark seen sets and suppress queued copies.
    [Test]
    public async Task Gossip_aggregate_marks_the_seen_sets_only_once_fork_choice_accepts_it([Values] bool accepted)
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Importer.AcceptsGossipOperations = accepted;
        harness.Orchestrator.RouteGossipEvents();
        Assert.That(Handle(Aggregate(participants: 2, aggregator: 7)), Is.EqualTo(MessageValidity.Ignored), "fixture: the aggregate is raised");
        Assert.That(Handle(Aggregate(participants: 1, aggregator: 8)), Is.EqualTo(MessageValidity.Ignored), "fixture: the covered copy is raised before the first verifies");

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        long duplicatesBefore = router.GetDropCount(GossipDropReason.Duplicate);
        Handle(Aggregate(participants: 1, aggregator: 9));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(accepted ? 1 : 2), "a queued copy the accepted aggregate covers is not verified");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate) - duplicatesBefore, Is.EqualTo(accepted ? 1 : 0), "only an accepted aggregate covers later copies");
        }

        MessageValidity Handle(SignedAggregateAndProofGloas aggregate) =>
            router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(aggregate));

        SignedAggregateAndProofGloas Aggregate(int participants, ulong aggregator)
        {
            SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(slot, participants: participants);
            aggregate.Message!.AggregatorIndex = aggregator;
            return aggregate;
        }
    }

    /// <summary>
    /// gloas/fork-choice.md <c>on_payload_attestation_message</c> checks a gossip vote against the store's current slot, so every vote
    /// queued before a slot tick must reach fork choice before that tick moves the store to the next slot, however many are queued,
    /// and every vote queued after the tick must reach it after the tick.
    /// </summary>
    [Test]
    public async Task Payload_attestations_reach_fork_choice_in_order_with_a_slot_tick(
        [Values(1, BeaconSyncOrchestrator.VotesPerPass, BeaconSyncOrchestrator.VotesPerPass + 3)] int votesBeforeTick)
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        int ticksBefore = harness.Importer.Ticks.Count;
        const int votesAfterTick = 2;
        for (ulong validator = 0; validator < (ulong)(votesBeforeTick + votesAfterTick); validator++)
        {
            if (validator == (ulong)votesBeforeTick)
            {
                await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None);
            }

            Assert.That(PtcVote(router, slot, validator), Is.EqualTo(MessageValidity.Ignored), "fixture: the vote is raised");
        }

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Ticks, Has.Count.EqualTo(ticksBefore + 1), "fixture: the tick was processed");
            Assert.That(harness.Importer.TicksAtGossipOperations,
                Is.EqualTo(Enumerable.Repeat(ticksBefore, votesBeforeTick).Concat(Enumerable.Repeat(ticksBefore + 1, votesAfterTick))));
        }
    }

    /// <summary>
    /// A tick skipped behind a newer one never ticks fork choice by itself, yet a vote queued after it is checked against that slot
    /// (gloas/fork-choice.md <c>on_payload_attestation_message</c>): fork choice must reach the skipped tick before the vote, not the newer tick.
    /// </summary>
    [Test]
    public async Task Payload_attestation_queued_after_a_skipped_tick_reaches_fork_choice_at_that_tick()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        int ticksBefore = harness.Importer.Ticks.Count;
        await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None);
        Assert.That(PtcVote(router, slot, validatorIndex: 1), Is.EqualTo(MessageValidity.Ignored), "fixture: the vote is raised");
        await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 2, CancellationToken.None);

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Ticks.Skip(ticksBefore), Is.EqualTo((ulong[])[WallSlot + 1, WallSlot + 2]), "the skipped tick is applied for its vote, then the newest");
            Assert.That(harness.Importer.TicksAtGossipOperations, Is.EqualTo((int[])[ticksBefore + 1]), "the vote is verified after the skipped tick and before the newest");
        }
    }

    /// <summary>Votes stamped with a tick stuck behind a full work channel cannot be read yet, so only a bounded number may wait; earlier votes stay queued.</summary>
    [Test]
    public async Task Votes_stamped_with_a_tick_waiting_on_a_full_work_channel_are_bounded()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        const int earlyVotes = 3;
        for (ulong validator = 0; validator < earlyVotes; validator++)
        {
            PtcVote(router, slot, validator);
        }

        while (harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.SlotTickItem(1)))
        {
        }

        Task pendingTick = harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None).AsTask();
        Assert.That(pendingTick.IsCompleted, Is.False, "fixture: the tick waits on the full work channel");

        ulong droppedBefore = Metrics.BeaconChainGossipDropped;
        for (ulong validator = earlyVotes; validator < earlyVotes + BeaconSyncOrchestrator.VoteQueueCapacity; validator++)
        {
            PtcVote(router, slot, validator);
        }

        ulong dropped = Metrics.BeaconChainGossipDropped - droppedBefore;
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        await pendingTick.WaitAsync(TimeSpan.FromSeconds(10));
        for (int pass = 0; pass < 10; pass++)
        {
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dropped, Is.EqualTo((ulong)(BeaconSyncOrchestrator.VoteQueueCapacity - BeaconSyncOrchestrator.MaxVotesAheadOfTick)), "votes past the bound are dropped");
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(earlyVotes + BeaconSyncOrchestrator.MaxVotesAheadOfTick), "the votes queued before the tick and the bounded ones are verified");
        }
    }

    /// <summary>A tick skipped behind a newer stamped one that never reached the work channel still ticks fork choice before the vote read by the next pass.</summary>
    [Test]
    public async Task Payload_attestation_read_in_a_later_pass_still_reaches_fork_choice_at_the_skipped_tick()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        int ticksBefore = harness.Importer.Ticks.Count;
        await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 1, CancellationToken.None);
        Assert.That(PtcVote(router, slot, validatorIndex: 1), Is.EqualTo(MessageValidity.Ignored), "fixture: the vote is raised");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await harness.Orchestrator.EnqueueSlotTickAsync(WallSlot + 2, cancelled.Token), "fixture: the newer tick is stamped but never queued");

        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Ticks.Skip(ticksBefore), Is.EqualTo((ulong[])[WallSlot + 1]), "the skipped tick is applied for its vote");
            Assert.That(harness.Importer.TicksAtGossipOperations, Is.EqualTo((int[])[ticksBefore + 1]), "the vote is verified after that tick");
        }
    }

    /// <summary>
    /// The allowance for votes waiting on a tick is given back when they are read or refused, so it does not run out over the node's lifetime:
    /// after both paths ran, a full allowance of votes is queued without a drop.
    /// </summary>
    [Test]
    public async Task Votes_stamped_with_a_tick_do_not_use_up_the_bound_once_read_or_refused()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        ulong validator = 0;
        ulong nextTick = WallSlot + 1;

        async Task DrainAsync()
        {
            for (int pass = 0; pass < 2 + BeaconSyncOrchestrator.VoteQueueCapacity / BeaconSyncOrchestrator.VotesPerPass; pass++)
            {
                await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
            }
        }

        // Read path: votes stamped with a queued tick are read after it.
        await harness.Orchestrator.EnqueueSlotTickAsync(nextTick++, CancellationToken.None);
        for (int i = 0; i < 5; i++)
        {
            PtcVote(router, slot, validator++);
        }

        await DrainAsync();

        // Refused path: the vote channel is full when votes stamped with a queued tick arrive.
        for (int i = 0; i < BeaconSyncOrchestrator.VoteQueueCapacity; i++)
        {
            PtcVote(router, slot, validator++);
        }

        await harness.Orchestrator.EnqueueSlotTickAsync(nextTick++, CancellationToken.None);
        for (int i = 0; i < 10; i++)
        {
            PtcVote(router, slot, validator++);
        }

        await DrainAsync();

        await harness.Orchestrator.EnqueueSlotTickAsync(nextTick++, CancellationToken.None);
        ulong droppedBefore = Metrics.BeaconChainGossipDropped;
        for (int i = 0; i < BeaconSyncOrchestrator.MaxVotesAheadOfTick; i++)
        {
            PtcVote(router, slot, validator++);
        }

        Assert.That(Metrics.BeaconChainGossipDropped - droppedBefore, Is.Zero, "a full allowance of votes still fits behind a queued tick");
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

    // These tests are about queueing, so every validator index is a member of the slot's PTC; membership is covered with the router.
    private static readonly ulong[] EveryValidator = [.. Enumerable.Range(0, 1 << 16).Select(static i => (ulong)i)];
    private static readonly ConditionalWeakTable<GossipRouter, HashSet<ulong>> SlotsWithPtc = [];

    private static MessageValidity PtcVote(GossipRouter router, ulong slot, ulong validatorIndex, bool payloadPresent = true, byte signatureSeed = 0)
    {
        HashSet<ulong> slots = SlotsWithPtc.GetOrCreateValue(router);
        lock (slots)
        {
            if (slots.Add(slot))
            {
                router.SetPtc(slot, EveryValidator);
            }
        }

        return router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(new PayloadAttestationMessage
        {
            ValidatorIndex = validatorIndex,
            Data = new PayloadAttestationData { BeaconBlockRoot = Keccak.Compute("voted block"), Slot = slot, PayloadPresent = payloadPresent },
            Signature = new BlsSignature([.. Enumerable.Repeat(signatureSeed, BlsSignature.Length)]),
        })));
    }

    /// <summary>
    /// gloas/p2p-interface.md REJECTs a payload attestation from outside get_ptc(head state, slot): each head step tells the router the
    /// committee of the slots a vote can name, so the router tracks only members and a non-member costs fork choice nothing.
    /// </summary>
    [Test]
    public async Task Head_step_tells_the_router_the_ptc_so_only_members_reach_fork_choice()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(wallSlot: slot, router: router);
        harness.Importer.Ptc = _ => [7];
        harness.Orchestrator.RouteGossipEvents();
        MessageValidity beforeHeadStep = PtcVoteWithoutPtc(router, slot, validatorIndex: 7);

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        MessageValidity member = PtcVoteWithoutPtc(router, slot, validatorIndex: 7, signatureSeed: 1);
        MessageValidity nonMember = PtcVoteWithoutPtc(router, slot, validatorIndex: 8, signatureSeed: 1);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((beforeHeadStep, member, nonMember), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Ignored, MessageValidity.Rejected)));
            Assert.That(harness.Importer.GossipOperations.Cast<PayloadAttestationMessage>().Select(static vote => vote.ValidatorIndex), Is.EqualTo(new ulong[] { 7 }), "only the member, after the head step");
        }
    }

    /// <summary>
    /// A vote is accepted for the wall slot and, within the clock disparity, the slots either side of it (altair <c>is_current_slot</c>),
    /// so the head step must tell the router the committee of all three or a member's early or late vote is refused.
    /// </summary>
    [TestCase(-1, 100L, TestName = "head step tells the router the previous slot's committee")]
    [TestCase(0, 6000L, TestName = "head step tells the router the wall slot's committee")]
    [TestCase(1, 11700L, TestName = "head step tells the router the next slot's committee")]
    public async Task Head_step_tells_the_router_the_committee_of_the_slots_a_vote_can_name(int slotOffset, long millisecondsIntoWallSlot)
    {
        ulong wallSlot = FirstGloasSlot + 2;
        ulong slot = (ulong)((long)wallSlot + slotOffset);
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + wallSlot * Sepolia.SecondsPerSlot).AddMilliseconds(millisecondsIntoWallSlot))), LimboLogs.Instance);
        Harness harness = CreateHarness(wallSlot: wallSlot, router: router);
        harness.Importer.Ptc = committeeSlot => [committeeSlot];
        harness.Orchestrator.RouteGossipEvents();

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        MessageValidity member = PtcVoteWithoutPtc(router, slot, validatorIndex: slot);
        MessageValidity nonMember = PtcVoteWithoutPtc(router, slot, validatorIndex: slot + 1);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((member, nonMember), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Rejected)), "a known committee separates the member from the rest");
            Assert.That(harness.Importer.GossipOperations.Cast<PayloadAttestationMessage>().Select(static vote => vote.ValidatorIndex), Is.EqualTo(new[] { slot }));
        }
    }

    /// <summary>A head state that cannot tell the committee (a head not yet Gloas, an unknown head) must leave votes IGNOREd, not REJECTed: the sender is honest.</summary>
    [Test]
    public async Task Head_step_that_cannot_read_the_committee_leaves_the_votes_ignored()
    {
        ulong slot = FirstGloasSlot + 1;
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot + 9))), LimboLogs.Instance);
        Harness harness = CreateHarness(wallSlot: slot, router: router);
        harness.Importer.Ptc = _ => null;
        harness.Orchestrator.RouteGossipEvents();

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        MessageValidity vote = PtcVoteWithoutPtc(router, slot, validatorIndex: 7);
        await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);

        Assert.That((vote, harness.Importer.GossipOperations.Count), Is.EqualTo((MessageValidity.Ignored, 0)));
    }

    private static MessageValidity PtcVoteWithoutPtc(GossipRouter router, ulong slot, ulong validatorIndex, byte signatureSeed = 0) =>
        router.Handle(GossipTopics.PayloadAttestationMessage, gloasTopic: true, Snappy.CompressToArray(PayloadAttestationMessage.Encode(new PayloadAttestationMessage
        {
            ValidatorIndex = validatorIndex,
            Data = new PayloadAttestationData { BeaconBlockRoot = Keccak.Compute("voted block"), Slot = slot, PayloadPresent = true },
            Signature = new BlsSignature([.. Enumerable.Repeat(signatureSeed, BlsSignature.Length)]),
        })));
}
