// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

/// <summary>The attester slashing index rules and the Gloas same-slot vote rule that return a verdict without beacon state.</summary>
public partial class GossipRouterTests
{
    private static readonly ulong VoteSlot = FirstGloasSlot + 1;

    // phase0 attester_slashing: is_valid_indexed_attestation needs sorted, unique indices, and a REJECT follows the seen-index IGNORE.
    [TestCase(new ulong[] { 1, 2, 3 }, new ulong[] { 2, 3, 4 }, null, false, TestName = "slashing with sorted unique indices is consumed")]
    [TestCase(new ulong[] { 2, 1, 3 }, new ulong[] { 1, 2, 3 }, GossipDropReason.InvalidField, true, TestName = "slashing whose first attestation's indices are unsorted")]
    [TestCase(new ulong[] { 1, 2, 3 }, new ulong[] { 2, 2, 3 }, GossipDropReason.InvalidField, true, TestName = "slashing whose second attestation repeats an index")]
    [TestCase(new ulong[] { 2, 1 }, new ulong[] { 3, 4 }, GossipDropReason.InvalidField, false, TestName = "unsorted slashing with no common index only drops")]
    public void Attester_slashing_indices_must_be_sorted_and_unique(ulong[] indices1, ulong[] indices2, GossipDropReason? reason, bool rejected)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.AttesterSlashingReceived += (_, _) => received++;
        IndexedAttestation first = CreateIndexedAttestation(1, 4);
        IndexedAttestation second = CreateIndexedAttestation(2, 3);
        first.AttestingIndices = indices1;
        second.AttestingIndices = indices2;

        MessageValidity validity = router.HandleAttesterSlashing(Snappy.CompressToArray(AttesterSlashing.Encode(new AttesterSlashing { Attestation1 = first, Attestation2 = second })));

        AssertVerdict(router, validity, received, reason, rejected);
    }

    // gloas is_valid_indexed_attestation (EIP-7688): at most MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT attesting indices.
    [TestCase(0, null, TestName = "Gloas slashing at the attesting-index bound is consumed")]
    [TestCase(1, GossipDropReason.InvalidField, TestName = "Gloas slashing one index over the attesting-index bound")]
    public void Gloas_attester_slashing_indices_are_bounded(int overBound, GossipDropReason? reason)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.GloasAttesterSlashingReceived += (_, _) => received++;
        ulong[] indices = [.. Enumerable.Range(0, Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot + overBound).Select(static i => (ulong)i)];
        AttesterSlashingGloas slashing = GossipMessageValidatorTests.GloasSlashing(indices, [0], secondSource: 2, secondTarget: 3);

        MessageValidity validity = router.Handle(GossipTopics.AttesterSlashing, gloasTopic: true, GossipMessageValidatorTests.Encode(slashing));

        AssertVerdict(router, validity, received, reason, rejected: reason is not null);
    }

    [Test]
    public void Gloas_same_slot_payload_claim_requires_finalized_ancestry(
        [Values(0UL, 1UL)] ulong index, [Values] bool held, [Values] bool earlyNextSlot, [Values(0, 1)] int committeeIndex, [Values] bool finalizedAncestor)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        ulong voteSlot = earlyNextSlot ? VoteSlot + 1 : VoteSlot;
        Hash256 blockRoot = Keccak.Compute("voted block");
        if (held) store.PutForkedBlock(blockRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(voteSlot)));
        CheckpointRef finalized = new(0, finalizedAncestor ? Hash256.Zero : Keccak.Compute("other finalized branch"));
        ForkChoiceSnapshotHolder snapshots = new()
        {
            Current = new(finalized, finalized, Hash256.Zero,
                [new(0, Hash256.Zero, null, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero),
                 new(voteSlot, blockRoot, Hash256.Zero, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero)]),
        };
        SlotClock clock = new(Sepolia, new ManualTimestamper(SepoliaSlotStart(VoteSlot).AddSeconds(6)));
        ColumnGossipRouter headers = new(Sepolia, clock, LimboLogs.Instance, forkChoice: snapshots);
        GossipRouter router = new(Sepolia, clock, LimboLogs.Instance, store, headers: headers);
        int received = 0;
        router.GloasAggregateAndProofReceived += (_, _) => received++;

        SignedAggregateAndProofGloas aggregate = VoteFor(blockRoot, voteSlot, index);
        aggregate.Message!.Aggregate!.CommitteeBits!.SetAll(false);
        aggregate.Message.Aggregate.CommitteeBits[committeeIndex] = true;
        MessageValidity validity = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(aggregate));

        GossipDropReason? reason = !held ? GossipDropReason.UnknownBlock : index == 1 && finalizedAncestor && committeeIndex == 0 ? GossipDropReason.InvalidField : null;
        if (earlyNextSlot && reason is null && committeeIndex == 0)
        {
            Assert.That((validity, received), Is.EqualTo((MessageValidity.Ignored, 0)), "a valid early vote waits for its slot");
        }
        else
        {
            AssertVerdict(router, validity, received, reason, rejected: held && finalizedAncestor && index == 1 && committeeIndex == 0 && !earlyNextSlot);
        }
    }

    private static SignedAggregateAndProofGloas VoteFor(Hash256 blockRoot, ulong slot, ulong index)
    {
        SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(slot, index);
        aggregate.Message!.Aggregate!.Data!.BeaconBlockRoot = blockRoot;
        return aggregate;
    }

    private static (GossipRouter Router, BeaconChainStore Store) CreateSepoliaRouterWithStore()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        return (new GossipRouter(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(SepoliaSlotStart(VoteSlot).AddSeconds(6))), LimboLogs.Instance, store), store);
    }
}
