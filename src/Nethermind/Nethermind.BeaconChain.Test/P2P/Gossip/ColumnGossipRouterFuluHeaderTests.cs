// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Google.Protobuf;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Crypto;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.P2P.DataColumnSidecarTestFixture;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// fulu/p2p-interface.md data_column_sidecar_{subnet_id}: the KZG batch is ordered after the proposer signature. A header
// that is not an imported block's is free to mint and the pinned pubsub library has no peer scoring, so KZG work must run
// only under a header a validator signed, and a forgery must never shadow or crowd out the honest sidecar.
public class ColumnGossipRouterFuluHeaderTests
{
    private const ulong CurrentSlot = 13_410_304;
    private const ulong Column = 5;
    private const int Validators = 4;
    private const int Unsigned = -1;
    private const ulong OtherProposer = 3;
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly ulong FinalizedEpoch = Spec.GetEpoch(CurrentSlot) - 2;
    private static readonly ulong FinalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(FinalizedEpoch);
    private static readonly Hash256 ParentRoot = new(Enumerable.Repeat((byte)0x01, 32).ToArray());
    private static readonly Hash256 OtherRoot = new(Enumerable.Repeat((byte)0x03, 32).ToArray());
    private static readonly Hash256 UnseenRoot = new(Enumerable.Repeat((byte)0x04, 32).ToArray());
    private static readonly Hash256 MidRoot = new(Enumerable.Repeat((byte)0x05, 32).ToArray());
    private static readonly byte[] MasterSecretKey = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

    public enum Ancestry
    {
        NoSource,
        DescendsFromFinalized,
        OtherBranchFinalized,
        BlockNotInSnapshot,
        BlockAndParentNotInSnapshot,
        ParentAtSameSlot,
        ParentAncestryUnknown,
    }

    [Test]
    public void A_flood_of_forged_headers_runs_no_KZG_and_leaves_the_honest_sidecar_poolable()
    {
        const int forgeries = 3 * Eip7594DasConstants.NumberOfColumns;
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;

        List<MessageValidity> verdicts = [];
        for (int i = 0; i < forgeries; i++)
        {
            // Distinct header roots over valid cells, claiming both known and unknown proposer indices.
            DataColumnSidecar forged = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot, proposerIndex: (ulong)(i % (2 * Validators)));
            forged.SignedBlockHeader!.Message!.StateRoot = new Hash256(BitConverter.GetBytes(i).Concat(new byte[28]).ToArray());
            forged.SignedBlockHeader.Signature = new BlsSignature(Enumerable.Repeat((byte)(i + 1), BlsSignature.Length).ToArray());
            verdicts.Add(router.Handle(Column, gloasTopic: false, Message(forged)));
        }

        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        MessageValidity honestVerdict = router.Handle(Column, gloasTopic: false, Message(honest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts, Is.All.Not.EqualTo(MessageValidity.Accepted), "a forgery is never forwarded");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.InvalidHeaderSignature), Is.EqualTo(forgeries / 2), "a known proposer's forged signature is refused");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.UnknownProposer), Is.EqualTo(forgeries / 2), "a proposer with no known key is refused");
            Assert.That(honestVerdict, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(router.KzgBatchCount, Is.EqualTo(1), "the only KZG batch is the honest sidecar's");
            Assert.That(raised, Is.EqualTo(1), "only the honest sidecar is consumed");
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out _), Is.True, "the honest sidecar is pooled for availability");
        }
    }

    [Test]
    public void A_proposer_signing_more_headers_for_one_slot_than_the_limit_runs_no_KZG_under_the_extra_ones()
    {
        (ColumnGossipRouter router, _, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        List<MessageValidity> verdicts = [];
        for (int i = 0; i <= ColumnGossipRouter.SignedHeadersPerProposal; i++)
        {
            DataColumnSidecar equivocation = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
            equivocation.SignedBlockHeader!.Message!.StateRoot = new Hash256(Enumerable.Repeat((byte)(0x40 + i), 32).ToArray());
            verdicts.Add(router.Handle(Column, gloasTopic: false, Message(Signed(equivocation, signer: 0))));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Ignored));
            Assert.That(router.KzgBatchCount, Is.EqualTo(ColumnGossipRouter.SignedHeadersPerProposal), "no KZG batch past the limit");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.ProposerHeaderLimit), Is.EqualTo(1));
        }
    }

    [Test]
    public void Garbage_under_the_real_header_before_import_never_shadows_the_honest_copy()
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        DataColumnSidecar garbage = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        garbage.KzgProofs = [garbage.KzgProofs![1], garbage.KzgProofs[0]];

        MessageValidity first = router.Handle(Column, gloasTopic: false, Message(garbage));
        MessageValidity second = router.Handle(Column, gloasTopic: false, Message(honest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first, second), Is.EqualTo((MessageValidity.Rejected, MessageValidity.Ignored)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedKzgProofs), Is.EqualTo(1));
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out DataColumnSidecar? pooled), Is.True);
            Assert.That(pooled!.KzgProofs, Is.EqualTo(honest.KzgProofs), "the honest copy is pooled, the garbage is not");
        }
    }

    /// <summary>fulu/p2p-interface.md: [REJECT] the sidecar's block's parent passes validation; a parent the importer never recorded stays the IGNORE of an unseen one.</summary>
    [TestCase(true, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.FailedBlockValidation)]
    [TestCase(false, MessageValidity.Ignored, 1, true, null)]
    public void A_sidecar_whose_parent_failed_validation_is_rejected_before_any_KZG_work(bool parentFailed, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason)
    {
        FailedBlockRoots failedBlocks = new();
        failedBlocks.Add(parentFailed ? ParentRoot : OtherRoot, CurrentSlot - 1);
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true, failedBlocks: failedBlocks);
        DataColumnSidecar sidecar = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);

        AssertVerdict(router, pool, sidecar, expected, kzgBatches, consumed, reason);
    }

    [Test]
    public void Copies_under_an_imported_header_whose_column_already_verified_run_no_KZG([Values] bool verifiedOverGossip)
    {
        const int copies = 20;
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(Ancestry.DescendsFromFinalized);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        if (verifiedOverGossip)
        {
            router.Handle(Column, gloasTopic: false, Message(honest));
        }
        else
        {
            // As range sync adds a column it verified.
            pool.Add(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), CurrentSlot, honest);
        }

        StoreAsImported(store, honest);
        long batchesBefore = router.KzgBatchCount;
        DataColumnSidecar garbage = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        garbage.KzgProofs = [garbage.KzgProofs![1], garbage.KzgProofs[0]];
        MessageValidity[] verdicts = [.. Enumerable.Range(0, copies).Select(_ => router.Handle(Column, gloasTopic: false, Message(garbage)))];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(router.KzgBatchCount, Is.EqualTo(batchesBefore), "no KZG batch for a column already verified under this header");
            Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Ignored));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(copies));
        }
    }

    // A column sync pooled proves which cells and proofs are valid under its header, so a copy is judged against it without KZG.
    [Test]
    public void Metrics_A_copy_of_a_column_sync_already_pooled_runs_no_KZG_and_only_an_equal_copy_is_forwarded([Values] bool imported, [Values] bool alteredCopy, [Values] bool proposerCovered)
    {
        ProposerLookaheadHolder lookaheads = new() { Current = proposerCovered ? Lookahead(ParentRoot, 0) : null };
        // Import verified the proposer; otherwise only a covered expected proposer lets a copy be forwarded.
        bool forwardable = imported || proposerCovered;
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(Ancestry.DescendsFromFinalized, lookaheads: lookaheads);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        pool.Add(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), CurrentSlot, honest);
        if (imported)
        {
            StoreAsImported(store, honest);
        }

        ulong acceptedBefore = Metrics.BeaconChainGossipAccepted;
        MessageValidity first = router.Handle(Column, gloasTopic: false, Message(alteredCopy ? Altered(honest, 0) : honest));
        MessageValidity second = router.Handle(Column, gloasTopic: false, Message(honest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.BeaconChainGossipAccepted, Is.EqualTo(acceptedBefore + (forwardable ? 1UL : 0UL)));
            Assert.That(router.KzgBatchCount, Is.Zero);
            Assert.That((first, second), Is.EqualTo(!forwardable ? (MessageValidity.Ignored, MessageValidity.Ignored)
                : alteredCopy ? (MessageValidity.Ignored, MessageValidity.Accepted)
                : (MessageValidity.Accepted, MessageValidity.Ignored)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(forwardable ? 1 : 2));
        }
    }

    // With no key cache no signature rule applies, so the forged header here passes every check this router can run.
    [Test]
    public void A_forged_header_claiming_the_honest_slot_and_proposer_never_shadows_the_honest_sidecar([Values(1, Eip7594DasConstants.RequiredColumnsForReconstruction)] int forgedColumns)
    {
        const ulong honestColumn = Eip7594DasConstants.RequiredColumnsForReconstruction;
        (ColumnGossipRouter router, _, BeaconChainStore store) = Create(Ancestry.NoSource, subnets: AllSubnets, withPubkeys: false);
        IEnumerable<ulong> forgedIndices = forgedColumns == 1 ? [honestColumn] : Enumerable.Range(0, forgedColumns).Select(i => (ulong)i);

        // Valid cells under another state root: a header with the honest (slot, proposer_index) that is not the honest one.
        foreach (ulong index in forgedIndices)
        {
            DataColumnSidecar forged = DataColumnSidecarTestFixture.BuildValidSidecar(index, CurrentSlot);
            forged.SignedBlockHeader!.Message!.StateRoot = OtherRoot;
            router.Handle(index, gloasTopic: false, Message(forged));
        }

        DataColumnSidecar honest = DataColumnSidecarTestFixture.BuildValidSidecar(honestColumn, CurrentSlot);
        StoreAsImported(store, honest);

        Assert.That(router.Handle(honestColumn, gloasTopic: false, Message(honest)), Is.EqualTo(MessageValidity.Accepted));
    }

    // fulu/das-core.md "Reconstruction and cross-seeding": a reconstructed column of a subscribed subnet is sent to its mesh, so an equal gossip copy is a duplicate.
    // A failed send must not mark it, or the gossip copy, the only other way this node relays the column, would be dropped too.
    [Test]
    public void A_reconstructed_column_is_published_once_and_an_equal_gossip_copy_is_forwarded_only_when_the_publish_failed([Values] bool publishFails)
    {
        const ulong reconstructedColumn = Eip7594DasConstants.RequiredColumnsForReconstruction;
        Dictionary<string, SilentTopic> topics = [];
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: false), AllSubnets,
            getTopic: id => topics[id] = new SilentTopic { Fails = publishFails });
        StoreAsImported(store, DataColumnSidecarTestFixture.BuildValidSidecar(0, CurrentSlot));
        MessageValidity[] received = [.. Enumerable.Range(0, Eip7594DasConstants.RequiredColumnsForReconstruction)
            .Select(i => router.Handle((ulong)i, gloasTopic: false, Message(DataColumnSidecarTestFixture.BuildValidSidecar((ulong)i, CurrentSlot))))];
        long batchesBefore = router.KzgBatchCount;
        DataColumnSidecar honest = DataColumnSidecarTestFixture.BuildValidSidecar(reconstructedColumn, CurrentSlot);
        bool reconstructed = pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), reconstructedColumn, out _);
        string reconstructedTopic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.DataColumnSidecarTopicName(reconstructedColumn));

        DataColumnSidecar forged = DataColumnSidecarTestFixture.BuildValidSidecar(reconstructedColumn, CurrentSlot);
        forged.SignedBlockHeader!.Message!.StateRoot = OtherRoot;
        MessageValidity forgedVerdict = router.Handle(reconstructedColumn, gloasTopic: false, Message(forged));
        MessageValidity first = router.Handle(reconstructedColumn, gloasTopic: false, Message(honest));
        MessageValidity second = router.Handle(reconstructedColumn, gloasTopic: false, Message(honest));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received, Is.All.EqualTo(MessageValidity.Accepted));
            Assert.That(reconstructed, Is.True, "the column was reconstructed before any copy of it arrived");
            Assert.That(topics[reconstructedTopic].Published, Is.EqualTo(publishFails ? Array.Empty<byte[]>() : new[] { Message(honest) }), "the reconstructed sidecar is sent once, as its snappy SSZ");
            Assert.That(forgedVerdict, Is.Not.EqualTo(MessageValidity.Accepted));
            Assert.That((first, second), Is.EqualTo(publishFails ? (MessageValidity.Accepted, MessageValidity.Ignored) : (MessageValidity.Ignored, MessageValidity.Ignored)),
                "a published column is never forwarded again; an unpublished one is forwarded once");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.EqualTo(publishFails ? 1 : 3), "a published tuple also drops a forged copy claiming it");
            Assert.That(router.KzgBatchCount, Is.EqualTo(batchesBefore));
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(forged.SignedBlockHeader.Message), reconstructedColumn, out _), Is.False);
        }
    }

    [Test]
    public async Task Invalid_signature_on_an_imported_header_charges_only_with_available_keys([Values] bool withPubkeys)
    {
        (ColumnGossipRouter router, _, BeaconChainStore store) = Create(Ancestry.DescendsFromFinalized, withPubkeys: withPubkeys);
        DataColumnSidecar sidecar = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        StoreAsImported(store, sidecar);
        sidecar.SignedBlockHeader!.Signature = default;
        byte[] payload = Message(sidecar);
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.DataColumnSidecarTopicName(Column), payload, verdict =>
        {
            verdict.Complete(router.Handle(Column, gloasTopic: false, payload));
            return Task.CompletedTask;
        }, withPubkeys ? MessageValidity.Rejected : MessageValidity.Ignored);
        Assert.That(router.KzgBatchCount, Is.Zero, "a bad or unverifiable signature must not buy KZG work");
    }

    private static IEnumerable<TestCaseData> ImportedCases()
    {
        yield return Case("sidecar of an imported block is accepted", null, Ancestry.DescendsFromFinalized, MessageValidity.Accepted, kzgBatches: 1, consumed: true, null);
        yield return Case("sidecar of an imported block with no fork-choice source is accepted", null, Ancestry.NoSource, MessageValidity.Accepted, kzgBatches: 1, consumed: true, null);
        yield return Case("tampered inclusion proof on an imported block is rejected before KZG",
            static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, kzgBatches: 0, consumed: false, ColumnGossipDropReason.FailedInclusionProof);
        yield return Case("swapped KZG proofs on an imported block are rejected",
            static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]], Ancestry.DescendsFromFinalized, MessageValidity.Rejected, kzgBatches: 1, consumed: false, ColumnGossipDropReason.FailedKzgProofs);
        yield return Case("invalid signature over an imported header is rejected before KZG",
            static s => s.SignedBlockHeader!.Signature = new BlsSignature(Enumerable.Repeat((byte)0xAB, BlsSignature.Length).ToArray()), Ancestry.DescendsFromFinalized, MessageValidity.Rejected, kzgBatches: 0, consumed: false, ColumnGossipDropReason.HeaderSignatureMismatch);
        yield return Case("imported block off the finalized branch is rejected before KZG", null, Ancestry.OtherBranchFinalized, MessageValidity.Rejected, kzgBatches: 0, consumed: false, ColumnGossipDropReason.NotFinalizedDescendant);
        // The stored block's signature is not BLS-valid here, so a consumed sidecar shows import's verification is trusted.
        yield return Case("imported block the snapshot does not hold yet is accepted through its parent", null, Ancestry.BlockNotInSnapshot, MessageValidity.Accepted, kzgBatches: 1, consumed: true, null);
        yield return Case("imported block whose parent the snapshot does not hold yet is consumed but not forwarded", null, Ancestry.BlockAndParentNotInSnapshot, MessageValidity.Ignored, kzgBatches: 1, consumed: true, null);
    }

    [TestCaseSource(nameof(ImportedCases))]
    public void Sidecar_of_an_imported_block_follows_the_spec_order(Action<DataColumnSidecar>? mutate, Ancestry ancestry, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(ancestry);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
        StoreAsImported(store, sidecar);
        mutate?.Invoke(sidecar);

        AssertVerdict(router, pool, sidecar, expected, kzgBatches, consumed, reason);
    }

    private static IEnumerable<TestCaseData> UnimportedCases()
    {
        yield return Unimported("signed header with a valid parent is consumed but not forwarded", null, 0, Ancestry.DescendsFromFinalized, MessageValidity.Ignored, 1, true, null);
        yield return Unimported("signed header with an unseen parent is consumed but not forwarded",
            static s => s.SignedBlockHeader!.Message!.ParentRoot = UnseenRoot, 0, Ancestry.DescendsFromFinalized, MessageValidity.Ignored, 1, true, null);
        yield return Unimported("header signed by another validator than its proposer index is rejected before KZG", null, 1, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.InvalidHeaderSignature);
        yield return Unimported("unsigned header is rejected before KZG", null, Unsigned, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.InvalidHeaderSignature);
        yield return Unimported("unsigned header with an unseen parent is dropped before KZG",
            static s => s.SignedBlockHeader!.Message!.ParentRoot = UnseenRoot, Unsigned, Ancestry.DescendsFromFinalized, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.InvalidHeaderSignature);
        yield return Unimported("header with a proposer index outside the key cache is dropped before KZG",
            static s => s.SignedBlockHeader!.Message!.ProposerIndex = Validators, Unsigned, Ancestry.DescendsFromFinalized, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.UnknownProposer);
        yield return Unimported("signed header not above its parent's slot is rejected before KZG", null, 0, Ancestry.ParentAtSameSlot, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.NotAboveParentSlot);
        yield return Unimported("signed header whose parent is off the finalized branch is rejected before KZG", null, 0, Ancestry.OtherBranchFinalized, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.NotFinalizedDescendant);
        yield return Unimported("tampered inclusion proof under a signed header is rejected before KZG",
            static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero, 0, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.FailedInclusionProof);
        yield return Unimported("tampered inclusion proof with an unseen parent is dropped before KZG",
            static s => { s.SignedBlockHeader!.Message!.ParentRoot = UnseenRoot; s.KzgCommitmentsInclusionProof![0] = Hash256.Zero; }, 0, Ancestry.DescendsFromFinalized, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.FailedInclusionProof);
        yield return Unimported("swapped KZG proofs under a signed header are rejected",
            static s => s.KzgProofs = [s.KzgProofs![1], s.KzgProofs[0]], 0, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, 1, false, ColumnGossipDropReason.FailedKzgProofs);
        yield return Unimported("tampered inclusion proof under a parent of unknown ancestry is dropped before KZG",
            static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero, 0, Ancestry.ParentAncestryUnknown, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.FailedInclusionProof);
        yield return Unimported("tampered inclusion proof with no key cache is rejected before KZG",
            static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero, Unsigned, Ancestry.DescendsFromFinalized, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.FailedInclusionProof, withPubkeys: false);
    }

    [TestCaseSource(nameof(UnimportedCases))]
    public void Sidecar_of_a_header_this_node_has_not_imported_is_never_forwarded(Action<DataColumnSidecar>? mutate, int signer, bool withPubkeys, Ancestry ancestry, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason)
    {
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(ancestry, parentInSnapshot: true, withPubkeys: withPubkeys);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
        mutate?.Invoke(sidecar);

        AssertVerdict(router, pool, Signed(sidecar, signer), expected, kzgBatches, consumed, reason);
    }

    [Test]
    public void Another_signature_over_a_header_that_already_verified_is_rejected()
    {
        (ColumnGossipRouter router, _, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        router.Handle(Column, gloasTopic: false, Message(Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0)));
        DataColumnSidecar resigned = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 1);

        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(resigned));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.InvalidHeaderSignature), Is.EqualTo(1));
        }
    }

    [TestCase(true, true, TestName = "signed header on a deep chain from the finalized root is consumed")]
    [TestCase(false, false, TestName = "signed header on a deep chain from another root is rejected")]
    public void Finalized_ancestry_is_read_across_a_deep_unfinalized_chain(bool chainFromFinalized, bool consumed)
    {
        const int depth = 4096;
        List<ForkChoiceSnapshotNode> nodes = [Node(FinalizedSlot, ParentRoot, null), Node(FinalizedSlot, OtherRoot, null)];
        Hash256 tip = chainFromFinalized ? ParentRoot : OtherRoot;
        for (int i = 1; i <= depth; i++)
        {
            byte[] rootBytes = new byte[Hash256.Size];
            BitConverter.TryWriteBytes(rootBytes, i);
            rootBytes[^1] = 0xEE;
            Hash256 root = new(rootBytes);
            // Every chain node sits after the finalized slot and before the sidecar's.
            nodes.Add(Node(FinalizedSlot + 1, root, tip));
            tip = root;
        }

        CheckpointRef finalized = new(FinalizedEpoch, ParentRoot);
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(new ForkChoiceSnapshot(finalized, finalized, Hash256.Zero, nodes));
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
        sidecar.SignedBlockHeader!.Message!.ParentRoot = tip;

        AssertVerdict(router, pool, Signed(sidecar, signer: 0), consumed ? MessageValidity.Ignored : MessageValidity.Rejected, kzgBatches: consumed ? 1 : 0, consumed,
            consumed ? null : ColumnGossipDropReason.NotFinalizedDescendant);
    }

    [Test]
    public void The_container_router_reads_the_lookahead_the_importer_publishes_and_the_shared_key_cache([Values] bool expectedProposer)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        ContainerBuilder builder = BeaconChainTestContainer.Builder();
        builder.AddSingleton<ITimestamper>(new ManualTimestamper(now));
        using IContainer container = builder.Build();
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        ProposerLookaheadHolder lookaheads = container.Resolve<ProposerLookaheadHolder>();
        container.Resolve<IBlockImporterFactory>().Create(new ForkedBeaconState.OfFulu(chain.AnchorState), new ForkedSignedBeaconBlock.OfFulu(chain.AnchorBlock), chain.AnchorRoot).ComputeHead();
        Hash256? publishedDependentRoot = lookaheads.Current?.DependentRoot;

        container.Resolve<ForkChoiceSnapshotHolder>().Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        lookaheads.Current = Lookahead(ParentRoot, expectedProposer ? 0UL : 1UL);
        container.Resolve<PubkeyCache>().Build(KeyedValidators());
        ColumnGossipRouter router = container.Resolve<ColumnGossipRouter>();
        router.Start(_ => new SilentTopic(), ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), [Column]);
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;

        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(publishedDependentRoot, Is.EqualTo(chain.AnchorRoot), "the importer publishes into the container's holder");
            // Accepted only if the router reads both the lookahead and the key cache the test wrote.
            Assert.That(verdict, Is.EqualTo(expectedProposer ? MessageValidity.Accepted : MessageValidity.Rejected));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.UnexpectedProposer), Is.EqualTo(expectedProposer ? 0 : 1));
            Assert.That((raised, router.KzgBatchCount), Is.EqualTo(expectedProposer ? (1, 1L) : (0, 0L)));
        }
    }

    [Test]
    public void The_container_router_and_importer_factory_share_one_set_of_failed_blocks()
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        ContainerBuilder builder = BeaconChainTestContainer.Builder();
        builder.AddSingleton<ITimestamper>(new ManualTimestamper(now));
        IEngineDriver engine = Substitute.For<IEngineDriver>();
        engine.NotifyNewPayload(Arg.Any<BeaconBlockBody>()).Returns(ExecutionStatus.Valid);
        builder.AddSingleton(engine);
        using IContainer container = builder.Build();
        UnsignedChain unsigned = UnsignedChain.Create();
        IBlockImporter importer = container.Resolve<IBlockImporterFactory>().Create(new ForkedBeaconState.OfFulu(unsigned.Anchor.AnchorState), new ForkedSignedBeaconBlock.OfFulu(unsigned.Anchor.AnchorBlock), unsigned.AnchorRoot);
        UnsignedChain.ChainBlock broken = unsigned.Extend(unsigned.AnchorRoot, slot: 1, payloadHashByte: 0xb1);
        broken.Block.Message!.StateRoot = Keccak.Compute("wrong state root");
        Hash256 brokenRoot = SszRoots.HashTreeRoot(broken.Block.Message);
        container.Resolve<ForkChoiceSnapshotHolder>().Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        container.Resolve<PubkeyCache>().Build(KeyedValidators());
        ColumnGossipRouter router = container.Resolve<ColumnGossipRouter>();
        router.Start(_ => new SilentTopic(), ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), [Column]);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
        sidecar.SignedBlockHeader!.Message!.ParentRoot = brokenRoot;

        BlockImportResult imported = importer.Import(new ForkedSignedBeaconBlock.OfFulu(broken.Block), brokenRoot, verifySignatures: false);
        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(Signed(sidecar, signer: 0)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported, Is.EqualTo(BlockImportResult.Invalid), "fixture");
            Assert.That(verdict, Is.EqualTo(MessageValidity.Rejected), "the importer's refusal reaches the router through the container");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.FailedBlockValidation), Is.EqualTo(1));
            Assert.That(router.KzgBatchCount, Is.Zero);
        }
    }

    private static IEnumerable<TestCaseData> ExpectedProposerCases()
    {
        ulong epoch = Spec.GetEpoch(CurrentSlot);
        yield return Proposer("header of the expected proposer passes to KZG and is forwarded", epoch, ParentRoot, 0, 0, MessageValidity.Accepted, 1, true, null);
        yield return Proposer("header of another proposer than the expected one is rejected before KZG", epoch, ParentRoot, 2, 0, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.UnexpectedProposer);
        yield return Proposer("header on a branch whose dependent root differs is dropped before KZG", epoch, OtherRoot, 0, 0, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.ProposerNotVerifiable);
        yield return Proposer("header on a branch whose dependent root differs signed by another validator is rejected before KZG", epoch, OtherRoot, 0, 1, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.InvalidHeaderSignature);
        yield return Proposer("tampered inclusion proof on a branch whose dependent root differs is rejected before KZG", epoch, OtherRoot, 0, 0, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.FailedInclusionProof,
            static s => s.KzgCommitmentsInclusionProof![0] = Hash256.Zero);
        yield return Proposer("header in an epoch the lookahead does not cover is dropped before KZG", epoch - 2, ParentRoot, 0, 0, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.ProposerNotVerifiable);
        yield return Proposer("header with an unseen parent is dropped before KZG", epoch, ParentRoot, 0, 0, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.ProposerNotVerifiable,
            static s => s.SignedBlockHeader!.Message!.ParentRoot = UnseenRoot);
        yield return Proposer("header before any lookahead is published is dropped before KZG", null, ParentRoot, 0, 0, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.ProposerNotVerifiable);
        yield return Proposer("header of the expected proposer signed by another validator is rejected before KZG", epoch, ParentRoot, 0, 1, MessageValidity.Rejected, 0, false, ColumnGossipDropReason.InvalidHeaderSignature);
        // A lookahead from the previous epoch's state, whose first slot the parent sits on: the dependent root is the parent's parent.
        yield return Proposer("header whose parent is in the lookahead walks to the dependent root below it", epoch - 1, ParentRoot, 0, 0, MessageValidity.Accepted, 1, true, null, ParentInLookahead);
        yield return Proposer("header whose parent is in the lookahead is not covered by the parent's root", epoch - 1, MidRoot, 0, 0, MessageValidity.Ignored, 0, false, ColumnGossipDropReason.ProposerNotVerifiable, ParentInLookahead);
    }

    [TestCaseSource(nameof(ExpectedProposerCases))]
    public void The_expected_proposer_is_checked_before_KZG(ulong? lookaheadEpoch, Hash256 dependentRoot, int expectedProposer, int signer, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason, Action<DataColumnSidecar>? mutate)
    {
        ForkChoiceSnapshot snapshot = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        snapshot = snapshot with { Nodes = [.. snapshot.Nodes, Node(BeaconStateAccessors.ComputeStartSlotAtEpoch(Spec.GetEpoch(CurrentSlot) - 1), MidRoot, ParentRoot)] };
        ProposerLookaheadHolder lookaheads = new() { Current = lookaheadEpoch is { } epoch ? Lookahead(dependentRoot, (ulong)expectedProposer, epoch) : null };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(snapshot, lookaheads: lookaheads);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);
        mutate?.Invoke(sidecar);

        AssertVerdict(router, pool, Signed(sidecar, signer), expected, kzgBatches, consumed, reason);
    }

    public enum ImportOrder
    {
        BeforeImport,
        AfterImport,
        UncoveredUntilImport,
        AncestryUnknownUntilImport,
    }

    [Test]
    public void Metrics_A_column_that_passes_every_check_is_forwarded_once([Values] ImportOrder order)
    {
        ProposerLookaheadHolder lookaheads = new() { Current = order == ImportOrder.UncoveredUntilImport ? null : Lookahead(ParentRoot, 0) };
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(order == ImportOrder.AncestryUnknownUntilImport ? Ancestry.ParentAncestryUnknown : Ancestry.DescendsFromFinalized, parentInSnapshot: false) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(null, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;
        if (order == ImportOrder.AfterImport)
        {
            StoreAsImported(store, honest);
        }

        ulong acceptedBefore = Metrics.BeaconChainGossipAccepted;
        MessageValidity first = router.Handle(Column, gloasTopic: false, Message(honest));
        StoreAsImported(store, honest);
        // The importer publishes a snapshot holding the block, whose finalized ancestry is then known.
        snapshots.Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: false);
        MessageValidity second = router.Handle(Column, gloasTopic: false, Message(honest));
        MessageValidity third = router.Handle(Column, gloasTopic: false, Message(honest));

        using (Assert.EnterMultipleScope())
        {
            MessageValidity[] expected = order is ImportOrder.UncoveredUntilImport or ImportOrder.AncestryUnknownUntilImport
                ? [MessageValidity.Ignored, MessageValidity.Accepted, MessageValidity.Ignored]
                : [MessageValidity.Accepted, MessageValidity.Ignored, MessageValidity.Ignored];
            // With ancestry unknown the first copy is consumed but not forwarded: two messages, two accepted outcomes.
            Assert.That(Metrics.BeaconChainGossipAccepted, Is.EqualTo(acceptedBefore + (order == ImportOrder.AncestryUnknownUntilImport ? 2UL : 1UL)));
            Assert.That(new[] { first, second, third }, Is.EqualTo(expected));
            Assert.That((raised, router.KzgBatchCount), Is.EqualTo((1, 1L)), "consumed once, verified once");
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out _), Is.True);
        }
    }

    // Every column of a block carries the same signed header and inclusion proof, so copies with altered cells cost nothing to make.
    [Test]
    public void Altered_copies_under_the_real_header_run_at_most_the_KZG_batch_bound([Values] bool imported, [Values(0, 1, 200)] int alteredBeforeHonest)
    {
        const int alteredAfterHonest = 200;
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(Ancestry.DescendsFromFinalized, lookaheads: lookaheads);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        Hash256 blockRoot = SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!);
        if (imported)
        {
            StoreAsImported(store, honest);
        }

        List<MessageValidity> altered = [.. Enumerable.Range(0, alteredBeforeHonest).Select(i => router.Handle(Column, gloasTopic: false, Message(Altered(honest, i))))];
        MessageValidity honestVerdict = router.Handle(Column, gloasTopic: false, Message(honest));
        long batchesThroughHonest = router.KzgBatchCount;
        bool withinBound = alteredBeforeHonest < ColumnGossipRouter.KzgBatchesPerColumn;
        if (!withinBound)
        {
            // As range sync adds a column it verified.
            pool.Add(blockRoot, CurrentSlot, honest);
        }

        altered.AddRange(Enumerable.Range(alteredBeforeHonest, alteredAfterHonest).Select(i => router.Handle(Column, gloasTopic: false, Message(Altered(honest, i)))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(altered, Is.All.Not.EqualTo(MessageValidity.Accepted));
            Assert.That(honestVerdict, Is.EqualTo(withinBound ? MessageValidity.Accepted : MessageValidity.Ignored));
            Assert.That(batchesThroughHonest, Is.EqualTo(withinBound ? alteredBeforeHonest + 1 : ColumnGossipRouter.KzgBatchesPerColumn));
            Assert.That(router.KzgBatchCount, Is.EqualTo(batchesThroughHonest), "no KZG batch once the column is held");
            Assert.That(pool.TryGet(blockRoot, Column, out DataColumnSidecar? pooled), Is.True);
            Assert.That(DataColumnSidecar.Encode(pooled!), Is.EqualTo(DataColumnSidecar.Encode(honest)), "the honest copy is pooled");
        }
    }

    public enum Resolution
    {
        ParentPublished,
        BlockImported,
        LookaheadPublished,
    }

    // fulu/p2p-interface.md: a sidecar whose expected proposer cannot be verified yet "MAY be queued for later processing".
    [Test]
    public void A_sidecar_whose_proposer_cannot_be_verified_yet_is_pooled_once_it_can([Values] Resolution resolution)
    {
        ForkChoiceSnapshot withParent = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        ForkChoiceSnapshotHolder snapshots = new() { Current = resolution == Resolution.ParentPublished ? Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) : withParent };
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(resolution == Resolution.ParentPublished ? ParentRoot : OtherRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, BeaconChainStore store) = Create(null, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        Hash256 blockRoot = SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!);
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;

        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(honest));
        MessageValidity beforePublish = router.Handle(Column, gloasTopic: false, UndecodableMessage);
        bool pooledBeforePublish = pool.TryGet(blockRoot, Column, out _);
        if (resolution == Resolution.BlockImported)
        {
            StoreAsImported(store, honest);
        }

        if (resolution == Resolution.LookaheadPublished)
        {
            lookaheads.Current = Lookahead(ParentRoot, 0);
        }
        else
        {
            snapshots.Current = withParent with { };
        }

        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((verdict, beforePublish, pooledBeforePublish), Is.EqualTo((MessageValidity.Ignored, MessageValidity.Rejected, false)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.ProposerNotVerifiable), Is.EqualTo(1), "verified on the retry");
            Assert.That((raised, router.KzgBatchCount), Is.EqualTo((1, 1L)));
            Assert.That(pool.TryGet(blockRoot, Column, out DataColumnSidecar? pooled) ? DataColumnSidecar.Encode(pooled!) : null, Is.EqualTo(DataColumnSidecar.Encode(honest)));
        }
    }

    /// <summary>
    /// A sidecar whose expected proposer cannot be verified yet is ignored at once, so while it is queued its message holds no pending
    /// verdict, or room for one, in the pubsub router; the retry that verifies it pools it and gives no other verdict.
    /// </summary>
    /// <remarks>fulu/p2p-interface.md: a sidecar whose proposer_index cannot immediately be verified MAY be queued; do not REJECT, instead IGNORE.</remarks>
    [Test]
    public void A_queued_sidecar_is_ignored_at_once_and_pooled_by_the_retry()
    {
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(OtherRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, lookaheads: lookaheads, forkChoice: new ForkChoiceSnapshotHolder { Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true) });
        SlotClock clock = new(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6)));
        GossipMessageValidator validator = new(new GossipRouter(Spec, clock, LimboLogs.Instance), router, Spec, clock);
        DataColumnSidecar sidecar = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        Hash256 blockRoot = SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!);
        List<MessageValidity> given = [];
        GossipVerdict verdict = new(validity => { given.Add(validity); return true; }, null);

        MessageValidity routed = validator.Validate(new Nethermind.Libp2p.Protocols.Pubsub.Dto.Message
        {
            Topic = GossipTopics.Topic(ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), GossipTopics.DataColumnSidecarTopicName(Column)),
            Data = ByteString.CopyFrom(Message(sidecar)),
        }, verdict);
        bool handedOff = verdict.IsHandedOff;
        bool pooledWhileQueued = pool.TryGet(blockRoot, Column, out _);
        lookaheads.Current = Lookahead(ParentRoot, 0);
        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((routed, handedOff, pooledWhileQueued), Is.EqualTo((MessageValidity.Ignored, false, false)), "the caller gives the IGNORE at once");
            Assert.That(given, Is.Empty, "the retry gives no verdict for a message already ignored");
            Assert.That(pool.TryGet(blockRoot, Column, out _), Is.True, "the retry pools the queued sidecar");
        }
    }

    /// <summary>
    /// A Fulu block is judged on its header as a sidecar is: accepted once the parent, slot, finalized-ancestor, expected-proposer and
    /// signature rules pass, before its data or execution payload, so it is forwarded and announced early; a REJECT is never raised for import,
    /// and a block those sources cannot check yet is ignored at once while it still imports.
    /// </summary>
    /// <remarks>phase0 p2p-interface.md beacon_block; the signing root of a block is its header's root.</remarks>
    [TestCase(0, 0UL, true, MessageValidity.Accepted, 1, TestName = "Fulu_block_header_that_verifies_is_accepted_at_once")]
    [TestCase(0, 1UL, true, MessageValidity.Rejected, 0, TestName = "Fulu_block_from_an_unexpected_proposer_is_rejected")]
    [TestCase(1, 0UL, true, MessageValidity.Rejected, 0, TestName = "Fulu_block_with_an_invalid_signature_is_rejected")]
    [TestCase(0, 0UL, false, MessageValidity.Ignored, 1, TestName = "Fulu_block_whose_proposer_cannot_be_checked_is_ignored_and_imported")]
    public void Fulu_block_header_is_checked_before_its_data(int signer, ulong expectedProposer, bool lookaheadOnBranch, MessageValidity expected, int raised)
    {
        (GossipRouter router, _) = BlockRouter(lookaheadOnBranch ? ParentRoot : OtherRoot, expectedProposer, out List<GossipVerdict> raisedVerdicts);

        (MessageValidity routed, List<MessageValidity> given) = HandleBlock(router, SignedBlock(signer, Hash256.Zero));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(given, Is.EqualTo(new[] { expected }));
            Assert.That(raisedVerdicts, Has.Count.EqualTo(raised), "only a block that is not rejected reaches the import pipeline");
            Assert.That(routed, Is.EqualTo(expected == MessageValidity.Rejected ? MessageValidity.Rejected : MessageValidity.Ignored));
        }
    }

    /// <summary>A child of a parent whose execution payload fork choice invalidated is ignored, not accepted, though its header verifies.</summary>
    /// <remarks>bellatrix p2p-interface.md beacon_block: the parent passes all validation, execution included; fork choice keeps an invalidated node.</remarks>
    [Test]
    public async Task Fulu_block_parent_ignore_precedes_later_rejections([Values] bool unknownParent, [Values] BlockFault fault)
    {
        CheckpointRef finalized = new(FinalizedEpoch, ParentRoot);
        ForkChoiceSnapshot snapshot = new(finalized, finalized, Hash256.Zero,
            unknownParent ? [] : [new ForkChoiceSnapshotNode(fault == BlockFault.ParentSlot ? CurrentSlot : FinalizedSlot,
                ParentRoot, null, FinalizedEpoch, FinalizedEpoch, 0, ExecutionStatus.Invalid, Hash256.Zero)]);
        (GossipRouter router, _) = BlockRouter(ParentRoot, fault == BlockFault.Proposer ? 1UL : 0UL, out _, snapshot);
        SignedBeaconBlock block = SignedBlock(0, Hash256.Zero);
        switch (fault)
        {
            case BlockFault.Timestamp:
                block.Message!.Body!.ExecutionPayload!.Timestamp++;
                break;
            case BlockFault.BlobCount:
                int count = (int)Spec.GetBlobParameters(Spec.GetEpoch(CurrentSlot))!.Value.MaxBlobsPerBlock + 1;
                block.Message!.Body!.BlobKzgCommitments = Enumerable.Range(0, count)
                    .Select(_ => SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength])).ToArray();
                break;
            case BlockFault.Signature:
                block.Signature = default;
                break;
            case BlockFault.ProposerIndex:
                block.Message!.ProposerIndex = ulong.MaxValue;
                break;
        }

        byte[] payload = Snappy.CompressToArray(SignedBeaconBlock.Encode(block));
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.BeaconBlock, payload, verdict =>
        {
            MessageValidity immediate = router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, payload, verdict);
            if (!verdict.IsHandedOff) verdict.Complete(immediate);
            return Task.CompletedTask;
        }, MessageValidity.Ignored);
    }

    public enum BlockFault { None, Timestamp, BlobCount, ParentSlot, Proposer, Signature, ProposerIndex }

    [Test]
    public async Task Fulu_block_fields_reject_after_parent_validation([Values] bool wrongTimestamp, [Values] bool lookaheadOnBranch)
    {
        (GossipRouter router, _) = BlockRouter(lookaheadOnBranch ? ParentRoot : OtherRoot, 0, out _);
        SignedBeaconBlock block = SignedBlock(0, Hash256.Zero);
        if (wrongTimestamp)
            block.Message!.Body!.ExecutionPayload!.Timestamp++;
        else
            block.Message!.Body!.BlobKzgCommitments = Enumerable.Range(0, (int)Spec.GetBlobParameters(Spec.GetEpoch(CurrentSlot))!.Value.MaxBlobsPerBlock + 1)
                .Select(_ => SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength])).ToArray();
        SignBlock(block, 0);
        byte[] payload = Snappy.CompressToArray(SignedBeaconBlock.Encode(block));
        await GossipRouterTests.AssertDeferredPeerPenaltyAsync(GossipTopics.BeaconBlock, payload, verdict =>
        {
            MessageValidity immediate = router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, payload, verdict);
            if (!verdict.IsHandedOff) verdict.Complete(immediate);
            return Task.CompletedTask;
        }, MessageValidity.Rejected);
    }

    /// <summary>
    /// A block accepted on its header that the import pipeline refuses for local load is throttled instead, and its claims are released, so a
    /// later copy is checked again and reaches import rather than being forwarded without ever being imported here.
    /// </summary>
    [Test]
    public void Fulu_block_refused_by_the_import_queue_is_throttled_and_accepted_on_a_later_copy()
    {
        (GossipRouter router, _) = BlockRouter(ParentRoot, 0, out List<GossipVerdict> raised);
        router.BeaconBlockReceived += (_, verdict) =>
        {
            if (raised.Count == 1)
            {
                verdict.Complete(MessageValidity.Throttled);
            }
        };
        SignedBeaconBlock block = SignedBlock(0, Hash256.Zero);

        (_, List<MessageValidity> first) = HandleBlock(router, block);
        (_, List<MessageValidity> second) = HandleBlock(router, block);

        Assert.That((first.Single(), second.Single(), raised.Count), Is.EqualTo((MessageValidity.Throttled, MessageValidity.Accepted, 2)));
    }

    /// <summary>phase0 p2p-interface.md beacon_block: [IGNORE] a block that is not the first with a valid signature for its (slot, proposer_index).</summary>
    [Test]
    public void Second_signed_block_of_a_proposer_for_a_slot_is_ignored()
    {
        (GossipRouter router, ColumnGossipRouter headers) = BlockRouter(ParentRoot, 0, out _);

        (_, List<MessageValidity> first) = HandleBlock(router, SignedBlock(0, Hash256.Zero));
        long verifiedBeforeSecond = headers.HeaderSignatureVerificationCount;
        (_, List<MessageValidity> second) = HandleBlock(router, SignedBlock(0, OtherRoot));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.Single(), second.Single()), Is.EqualTo((MessageValidity.Accepted, MessageValidity.Ignored)));
            Assert.That(headers.HeaderSignatureVerificationCount, Is.EqualTo(verifiedBeforeSecond), "the second block costs no BLS work");
        }
    }

    private static (GossipRouter Router, ColumnGossipRouter Headers) BlockRouter(Hash256 dependentRoot, ulong expectedProposer, out List<GossipVerdict> raised, ForkChoiceSnapshot? snapshot = null)
    {
        (ColumnGossipRouter headers, _, _) = Create(snapshot ?? Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true),
            lookaheads: new ProposerLookaheadHolder { Current = Lookahead(dependentRoot, expectedProposer) });
        GossipRouter router = new(Spec, new SlotClock(Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot + 6))),
            LimboLogs.Instance, headers: headers);
        List<GossipVerdict> verdicts = [];
        router.BeaconBlockReceived += (_, verdict) => verdicts.Add(verdict);
        raised = verdicts;
        return (router, headers);
    }

    // As DeferredGossipValidation does: the verdict of the checks that ran is given unless the import pipeline took the verdict over.
    private static (MessageValidity Routed, List<MessageValidity> Given) HandleBlock(GossipRouter router, SignedBeaconBlock block)
    {
        List<MessageValidity> given = [];
        GossipVerdict verdict = new(validity => { given.Add(validity); return true; }, null);
        MessageValidity routed = router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, Snappy.CompressToArray(SignedBeaconBlock.Encode(block)), verdict);
        if (!verdict.IsHandedOff)
        {
            verdict.Complete(routed);
        }

        return (routed, given);
    }

    /// <summary>A Fulu block on <see cref="ParentRoot"/> at <see cref="CurrentSlot"/> by validator 0, signed by <paramref name="signer"/>'s key.</summary>
    private static SignedBeaconBlock SignedBlock(int signer, Hash256 graffiti)
    {
        SignedBeaconBlock block = TestChain.CreateBlock(CurrentSlot, ParentRoot);
        block.Message!.ProposerIndex = 0;
        block.Message.Body!.Graffiti = graffiti;
        SignBlock(block, signer);
        return block;
    }

    private static void SignBlock(SignedBeaconBlock block, int signer)
    {
        Hash256 domain = Domains.ComputeDomain(DomainType.BeaconProposer, Spec.VersionForEpoch(Spec.GetEpoch(block.Message!.Slot)), Spec.GenesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block.Message), domain);
        block.Signature = new BlsSignature(BlsSigner.Sign(SecretKey(signer), signingRoot.Bytes).Bytes);
    }

    [Test]
    public void A_column_pooled_by_a_retry_is_not_validated_again_by_later_publications([Values(1, 3)] int laterSnapshots)
    {
        ForkChoiceSnapshot withParent = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) };
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        router.Handle(Column, gloasTopic: false, Message(honest));
        snapshots.Current = withParent with { };
        router.Handle(Column, gloasTopic: false, UndecodableMessage);
        long batchesAfterRetry = router.KzgBatchCount;
        bool pooledByRetry = pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out _);

        for (int i = 0; i < laterSnapshots; i++)
        {
            // A lookahead of another branch cannot verify the column, so a queued copy still there would be dropped as a duplicate.
            lookaheads.Current = Lookahead(OtherRoot, 0);
            router.Handle(Column, gloasTopic: false, UndecodableMessage);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That((pooledByRetry, batchesAfterRetry), Is.EqualTo((true, 1L)));
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.Zero, "the retried entry left the queue, so a later publication meets nothing to validate");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.ProposerNotVerifiable), Is.EqualTo(1));
            Assert.That(router.KzgBatchCount, Is.EqualTo(batchesAfterRetry));
        }
    }

    [Test]
    public void Queued_copies_of_a_column_are_bounded_and_keep_an_honest_copy_within_the_bound()
    {
        const int alteredAfterHonest = 50;
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) };
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        Hash256 blockRoot = SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!);

        router.Handle(Column, gloasTopic: false, Message(Altered(honest, 0)));
        router.Handle(Column, gloasTopic: false, Message(honest));
        for (int i = 1; i <= alteredAfterHonest; i++)
        {
            router.Handle(Column, gloasTopic: false, Message(Altered(honest, i)));
        }

        snapshots.Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(router.KzgBatchCount, Is.EqualTo(ColumnGossipRouter.KzgBatchesPerColumn), "only the queued copies are verified");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.Duplicate), Is.Zero, "copies past the bound were never queued, so the retry meets none of them");
            Assert.That(pool.TryGet(blockRoot, Column, out DataColumnSidecar? pooled) ? DataColumnSidecar.Encode(pooled!) : null, Is.EqualTo(DataColumnSidecar.Encode(honest)));
        }
    }

    [Test]
    public void A_header_no_key_cache_can_check_is_not_queued()
    {
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) };
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, withPubkeys: false, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar unsigned = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot);

        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(unsigned));
        snapshots.Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(router.KzgBatchCount, Is.Zero, "a queued copy would reach KZG once its proposer is covered");
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(unsigned.SignedBlockHeader!.Message!), Column, out _), Is.False);
        }
    }

    [Test]
    public void A_failed_signature_does_not_evict_the_verified_one_so_the_honest_signature_pairs_once()
    {
        (ColumnGossipRouter router, _, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        DataColumnSidecar resigned = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 1);

        router.Handle(Column, gloasTopic: false, Message(honest));
        router.Handle(Column, gloasTopic: false, Message(resigned));
        router.Handle(Column, gloasTopic: false, Message(honest));

        Assert.That(router.HeaderSignatureVerificationCount, Is.EqualTo(2), "one pairing per distinct signature; the honest one stays cached across the failed one");
    }

    [Test]
    public void A_header_signed_under_a_cached_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        Validator[] validators = KeyedValidators();
        if (offSubgroup)
        {
            validators[0].Pubkey = OffSubgroupKeys.WithTorsion(SecretKey(0));
        }

        PubkeyCache keys = new();
        keys.Build(validators);
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(Ancestry.DescendsFromFinalized, parentInSnapshot: true, keys: keys);
        DataColumnSidecar sidecar = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);

        MessageValidity verdict = router.Handle(Column, gloasTopic: false, Message(sidecar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(offSubgroup ? MessageValidity.Rejected : MessageValidity.Ignored), "the same signature verifies under the key inside the subgroup");
            Assert.That(router.KzgBatchCount, Is.EqualTo(offSubgroup ? 0 : 1));
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!), Column, out _), Is.EqualTo(!offSubgroup));
        }
    }

    [Test]
    public void One_proposer_index_cannot_fill_the_queue_and_evict_the_columns_of_another()
    {
        const ulong flooder = 1;
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) };
        // Starts an epoch early so the lookahead covers every slot the flood signs.
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0, Spec.GetEpoch(CurrentSlot) - 1) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, subnets: AllSubnets, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        router.Handle(Column, gloasTopic: false, Message(honest));

        // The inclusion proof does not cover the slot or the index, so one built sidecar serves every key; KZG never runs while queued.
        DataColumnSidecar template = DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot, proposerIndex: flooder);
        for (ulong slotsBack = 0; slotsBack < 4; slotsBack++)
        {
            for (ulong column = 0; column < Eip7594DasConstants.NumberOfColumns; column++)
            {
                DataColumnSidecar.Decode(DataColumnSidecar.Encode(template), out DataColumnSidecar flood);
                flood.Index = column;
                flood.SignedBlockHeader!.Message!.Slot = CurrentSlot - slotsBack;
                router.Handle(column, gloasTopic: false, Message(Signed(flood, signer: (int)flooder)));
            }
        }

        snapshots.Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out _), Is.True, "the other proposer's queued column survives the flood");
            Assert.That(router.GetDropCount(ColumnGossipDropReason.UnexpectedProposer), Is.EqualTo(ColumnGossipRouter.ParkedColumnsPerProposer), "the retry meets exactly the flooding proposer's share of the queue");
        }
    }

    [Test]
    public void Metrics_revalidating_a_queued_column_is_not_exported_as_a_received_message([Values] bool nestedMessage)
    {
        ForkChoiceSnapshotHolder snapshots = new() { Current = Snapshot(Ancestry.BlockAndParentNotInSnapshot, parentInSnapshot: true) };
        ProposerLookaheadHolder lookaheads = new() { Current = Lookahead(ParentRoot, 0, Spec.GetEpoch(CurrentSlot) - 1) };
        (ColumnGossipRouter router, DataColumnSidecarPool pool, _) = Create(null, subnets: AllSubnets, lookaheads: lookaheads, forkChoice: snapshots);
        DataColumnSidecar honest = Signed(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot), signer: 0);
        router.Handle(Column, gloasTopic: false, Message(honest));
        ulong acceptedBefore = Metrics.BeaconChainGossipAccepted;
        ulong droppedBefore = Metrics.BeaconChainGossipDropped;

        if (nestedMessage) router.DataColumnSidecarReceived += _ => router.Handle(Column, gloasTopic: false, UndecodableMessage);
        snapshots.Current = Snapshot(Ancestry.DescendsFromFinalized, parentInSnapshot: true);
        router.Handle(Column, gloasTopic: false, UndecodableMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(honest.SignedBlockHeader!.Message!), Column, out _), Is.True, "the retry accepted the queued column");
            Assert.That(Metrics.BeaconChainGossipAccepted, Is.EqualTo(acceptedBefore), "one received message is one outcome");
            Assert.That(Metrics.BeaconChainGossipDropped, Is.EqualTo(droppedBefore + (nestedMessage ? 2UL : 1UL)), "only the undecodable messages are new");
        }
    }

    private static byte[] UndecodableMessage => Snappy.CompressToArray([0x01, 0x02, 0x03]);

    private static DataColumnSidecar Altered(DataColumnSidecar honest, int variant)
    {
        DataColumnSidecar.Decode(DataColumnSidecar.Encode(honest), out DataColumnSidecar altered);
        // The low byte of the first field element, so the cell stays canonical and only its proof fails.
        altered.Column![0][31] ^= (byte)(1 + variant % 255);
        return altered;
    }

    private static ulong[] AllSubnets => [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(i => (ulong)i)];

    private static void AssertVerdict(ColumnGossipRouter router, DataColumnSidecarPool pool, DataColumnSidecar sidecar, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason)
    {
        int raised = 0;
        router.DataColumnSidecarReceived += _ => raised++;

        MessageValidity validity = router.Handle(Column, gloasTopic: false, Message(sidecar));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected));
            Assert.That(router.KzgBatchCount, Is.EqualTo(kzgBatches), "KZG batches run");
            Assert.That(raised, Is.EqualTo(consumed ? 1 : 0), "consumed");
            Assert.That(pool.TryGet(SszRoots.HashTreeRoot(sidecar.SignedBlockHeader!.Message!), Column, out _), Is.EqualTo(consumed), "pooled");
            if (reason is { } dropReason)
            {
                Assert.That(router.GetDropCount(dropReason), Is.EqualTo(1), "the drop is counted under its reason");
            }
        }
    }

    /// <summary>Stores a block under <paramref name="sidecar"/>'s header root with the header's signature, as import would have.</summary>
    /// <remarks>The store trusts its key as the block root, so the block body is not made to hash to the header's body root.</remarks>
    private static Bls.SecretKey SecretKey(int index) => new(new Bls.SecretKey(MasterSecretKey, Bls.ByteOrder.LittleEndian), (uint)index);

    private static PubkeyCache Pubkeys()
    {
        PubkeyCache pubkeys = new();
        pubkeys.Build(KeyedValidators());
        return pubkeys;
    }

    private static Validator[] KeyedValidators() => [.. Enumerable.Range(0, Validators).Select(i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(SecretKey(i)).Compress()) })];

    /// <summary>Signs the header of <paramref name="sidecar"/> with validator <paramref name="signer"/>'s key, whatever proposer index it claims.</summary>
    private static DataColumnSidecar Signed(DataColumnSidecar sidecar, int signer)
    {
        if (signer != Unsigned)
        {
            BeaconBlockHeader header = sidecar.SignedBlockHeader!.Message!;
            Hash256 domain = Domains.ComputeDomain(DomainType.BeaconProposer, Spec.VersionForEpoch(Spec.GetEpoch(header.Slot)), Spec.GenesisValidatorsRoot);
            Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
            sidecar.SignedBlockHeader.Signature = new BlsSignature(BlsSigner.Sign(SecretKey(signer), signingRoot.Bytes).Bytes);
        }

        return sidecar;
    }

    private static TestCaseData Case(string name, Action<DataColumnSidecar>? mutate, Ancestry ancestry, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason) =>
        new TestCaseData(mutate, ancestry, expected, kzgBatches, consumed, reason).SetName(name);

    private static TestCaseData Proposer(string name, ulong? lookaheadEpoch, Hash256 dependentRoot, int expectedProposer, int signer, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason, Action<DataColumnSidecar>? mutate = null) =>
        new TestCaseData(lookaheadEpoch, dependentRoot, expectedProposer, signer, expected, kzgBatches, consumed, reason, mutate).SetName(name);

    private static void ParentInLookahead(DataColumnSidecar sidecar) => sidecar.SignedBlockHeader!.Message!.ParentRoot = MidRoot;

    /// <summary>A lookahead from a state at <paramref name="epoch"/> naming <paramref name="expectedProposer"/> for <see cref="CurrentSlot"/> when it covers it, and another validator for every other slot.</summary>
    private static ProposerLookaheadSnapshot Lookahead(Hash256 dependentRoot, ulong expectedProposer, ulong? epoch = null)
    {
        ulong lookaheadEpoch = epoch ?? Spec.GetEpoch(CurrentSlot);
        ulong[] proposers = [.. Enumerable.Repeat(OtherProposer, (int)Presets.ProposerLookaheadSlots)];
        ulong offset = CurrentSlot - BeaconStateAccessors.ComputeStartSlotAtEpoch(lookaheadEpoch);
        if (offset < (ulong)proposers.Length)
        {
            proposers[offset] = expectedProposer;
        }

        return new ProposerLookaheadSnapshot(lookaheadEpoch, dependentRoot, proposers);
    }

    private static TestCaseData Unimported(string name, Action<DataColumnSidecar>? mutate, int signer, Ancestry ancestry, MessageValidity expected, int kzgBatches, bool consumed, ColumnGossipDropReason? reason, bool withPubkeys = true) =>
        new TestCaseData(mutate, signer, withPubkeys, ancestry, expected, kzgBatches, consumed, reason).SetName(name);

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool, BeaconChainStore Store) Create(Ancestry ancestry, bool parentInSnapshot = false, ulong[]? subnets = null, bool withPubkeys = true, ProposerLookaheadHolder? lookaheads = null, PubkeyCache? keys = null, FailedBlockRoots? failedBlocks = null) =>
        Create(ancestry == Ancestry.NoSource ? null : Snapshot(ancestry, parentInSnapshot), subnets, withSource: ancestry != Ancestry.NoSource, withPubkeys, lookaheads, keys: keys, failedBlocks: failedBlocks);

    private static (ColumnGossipRouter Router, DataColumnSidecarPool Pool, BeaconChainStore Store) Create(ForkChoiceSnapshot? snapshot, ulong[]? subnets = null, bool withSource = true, bool withPubkeys = true, ProposerLookaheadHolder? lookaheads = null, ForkChoiceSnapshotHolder? forkChoice = null, PubkeyCache? keys = null, FailedBlockRoots? failedBlocks = null, Func<string, ITopic>? getTopic = null)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        DataColumnSidecarPool pool = new();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        ForkChoiceSnapshotHolder? holder = forkChoice ?? (withSource ? new ForkChoiceSnapshotHolder { Current = snapshot } : null);
        ColumnGossipRouter router = new(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, pool, store, forkChoice: holder, pubkeys: keys ?? (withPubkeys ? Pubkeys() : null), proposerLookahead: lookaheads, failedBlocks: failedBlocks);
        router.Start(getTopic ?? (_ => new SilentTopic()), ForkDigest.Compute(Spec, Spec.GetEpoch(CurrentSlot)), subnets ?? [Column]);
        return (router, pool, store);
    }

    // The fixture header sits at CurrentSlot on ParentRoot; ParentRoot sits at the finalized epoch's start slot.
    private static ForkChoiceSnapshot Snapshot(Ancestry ancestry, bool parentInSnapshot)
    {
        Hash256 blockRoot = SszRoots.HashTreeRoot(DataColumnSidecarTestFixture.BuildValidSidecar(Column, CurrentSlot).SignedBlockHeader!.Message!);
        Hash256 finalizedRoot = ancestry == Ancestry.OtherBranchFinalized ? OtherRoot : ParentRoot;
        List<ForkChoiceSnapshotNode> nodes = [Node(FinalizedSlot, OtherRoot, null)];
        if (ancestry != Ancestry.BlockAndParentNotInSnapshot)
        {
            nodes.Add(Node(ancestry switch { Ancestry.ParentAtSameSlot => CurrentSlot, Ancestry.ParentAncestryUnknown => FinalizedSlot + 1, _ => FinalizedSlot }, ParentRoot, null));
        }

        if (!parentInSnapshot && ancestry is not (Ancestry.BlockNotInSnapshot or Ancestry.BlockAndParentNotInSnapshot))
        {
            nodes.Add(Node(CurrentSlot, blockRoot, ParentRoot));
        }

        CheckpointRef finalized = new(FinalizedEpoch, finalizedRoot);
        return new ForkChoiceSnapshot(finalized, finalized, Hash256.Zero, nodes);
    }

    private static ForkChoiceSnapshotNode Node(ulong slot, Hash256 root, Hash256? parent) =>
        new(slot, root, parent, FinalizedEpoch, FinalizedEpoch, 0, ExecutionStatus.Valid, Hash256.Zero);

    private static byte[] Message(DataColumnSidecar sidecar) => Snappy.CompressToArray(DataColumnSidecar.Encode(sidecar));

    private sealed class SilentTopic : ITopic
    {
        public event Action<PeerId, byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed => true;

        public bool Fails { get; init; }

        public List<byte[]> Published { get; } = [];

        public void Subscribe() { }

        public void Unsubscribe() { }

        public void Publish(byte[] value)
        {
            if (Fails)
            {
                throw new InvalidOperationException("Router has not been started");
            }

            Published.Add(value);
        }

        public void Publish(IMessage value) { }
    }
}
