// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.ForkChoice;

/// <summary>
/// The pure predicates behind <see cref="ForkChoiceRunner.ShouldOverrideForkchoiceUpdate"/> and
/// <see cref="ForkChoiceRunner.IsDataAvailable"/>, tested directly rather than only through a full
/// runner: both gates are silent-wrong risks (get_proposer_head returning the ordinary head instead
/// of ever re-org'ing, or on_block accepting a block whose blob data was never actually verified)
/// that a wrong boundary or a skipped cross-check would not otherwise surface as a build error.
/// </summary>
public class ForkChoiceRunnerTests
{
    [TestCase(0ul, false, TestName = "epoch_boundary_slot_is_unstable")]
    [TestCase(1ul, true, TestName = "mid_epoch_slot_is_stable")]
    [TestCase(31ul, true, TestName = "last_slot_of_epoch_is_stable")]
    [TestCase(32ul, false, TestName = "next_epoch_boundary_is_unstable")]
    public void IsShufflingStable_is_false_only_at_an_epoch_boundary(ulong slot, bool expected) =>
        Assert.That(ForkChoiceRunner.IsShufflingStable(slot, slotsPerEpoch: 32), Is.EqualTo(expected));

    [TestCase(0ul, 0ul, true, TestName = "same_epoch_is_ok")]
    [TestCase(64ul, 0ul, true, TestName = "exactly_the_max_gap_is_ok")]
    [TestCase(96ul, 0ul, false, TestName = "one_epoch_past_the_max_gap_is_not_ok")]
    public void IsFinalizationOk_allows_up_to_the_configured_epoch_gap(ulong slot, ulong finalizedEpoch, bool expected) =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot, finalizedEpoch, reorgMaxEpochsSinceFinalization: 2), Is.EqualTo(expected));

    // finalizedEpoch (5) can never legitimately exceed compute_epoch_at_slot(slot) (0) in a consistent
    // store; the ulong subtraction underflows to a huge gap, which must deny the reorg rather than
    // wrap around into a false "ok".
    [Test]
    public void IsFinalizationOk_fails_closed_if_finalized_epoch_somehow_outran_the_slot() =>
        Assert.That(ForkChoiceRunner.IsFinalizationOk(slot: 0, finalizedEpoch: 5, reorgMaxEpochsSinceFinalization: 2), Is.False);

    [TestCase(1ul, 2ul, 3ul, true, TestName = "consecutive_parent_head_and_proposal_slots")]
    [TestCase(0ul, 2ul, 3ul, false, TestName = "parent_is_not_immediately_before_head")]
    [TestCase(1ul, 2ul, 4ul, false, TestName = "head_is_not_immediately_before_the_proposal_slot")]
    public void IsSingleSlotReorg_requires_three_consecutive_slots(ulong parentSlot, ulong headSlot, ulong proposalSlot, bool expected) =>
        Assert.That(ForkChoiceRunner.IsSingleSlotReorg(parentSlot, headSlot, proposalSlot), Is.EqualTo(expected));

    [Test]
    public void IsDataAvailable_is_trivially_true_for_a_block_with_no_blob_commitments()
    {
        BeaconBlock block = TestChain.CreateBlock(slot: 1, parentRoot: Hash256.Zero).Message!;
        Hash256 root = SszRoots.HashTreeRoot(block);

        Assert.That(ForkChoiceRunner.IsDataAvailable(block, root, dataColumns: null, BeaconChainSpec.Mainnet), Is.True);
    }

    [Test]
    public void IsDataAvailable_rejects_a_block_with_commitments_and_no_columns()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out _);

        Assert.That(ForkChoiceRunner.IsDataAvailable(block, root, dataColumns: null, BeaconChainSpec.Mainnet), Is.False);
    }

    [Test]
    public void IsDataAvailable_rejects_fewer_than_the_full_column_set()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar[] columns = [MatchingSidecar(block, root, commitment, index: 0)];

        Assert.That(ForkChoiceRunner.IsDataAvailable(block, root, columns, BeaconChainSpec.Mainnet), Is.False,
            "one column out of NumberOfColumns must not count as available");
    }

    // HasExactlyOneSidecarPerColumn is tested directly, on bare Index values, rather than through
    // IsDataAvailable: DataColumnSidecarVerifier.Verify always fails on a hand-built sidecar (no test
    // fixture here has a real KZG proof), so routing through IsDataAvailable would make these pass
    // whether or not the count/duplicate/range check under test actually ran.

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_a_duplicate_index()
    {
        DataColumnSidecar[] columns = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new DataColumnSidecar { Index = 0 }; // every sidecar claims index 0

        Assert.That(ForkChoiceRunner.HasExactlyOneSidecarPerColumn(columns), Is.False,
            "128 sidecars all claiming column 0 must not be treated as the full 128-column set");
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_an_index_out_of_range()
    {
        DataColumnSidecar[] columns = FullIndexSet();
        columns[0].Index = (ulong)Eip7594DasConstants.NumberOfColumns; // one past the valid range

        Assert.That(ForkChoiceRunner.HasExactlyOneSidecarPerColumn(columns), Is.False);
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_too_few_sidecars() =>
        Assert.That(ForkChoiceRunner.HasExactlyOneSidecarPerColumn([new DataColumnSidecar { Index = 0 }]), Is.False);

    [Test]
    public void HasExactlyOneSidecarPerColumn_accepts_the_full_set_exactly_once_each() =>
        Assert.That(ForkChoiceRunner.HasExactlyOneSidecarPerColumn(FullIndexSet()), Is.True);

    private static DataColumnSidecar[] FullIndexSet()
    {
        DataColumnSidecar[] columns = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new DataColumnSidecar { Index = (ulong)i };
        return columns;
    }

    // These three exercise ForkChoiceRunner.MatchesBlock directly rather than through the full
    // IsDataAvailable: a hand-built sidecar can never pass DataColumnSidecarVerifier.Verify's real KZG
    // and inclusion-proof checks (no test fixture here has a valid trusted-setup proof), so routing
    // these through IsDataAvailable would make them pass whether or not the cross-check under test
    // actually ran - Verify's own, unrelated failure would mask a missing or broken cross-check.

    [Test]
    public void MatchesBlock_rejects_a_sidecar_addressed_to_a_different_block()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        sidecar.SignedBlockHeader!.Message!.Slot = 999; // changes the header's own hash tree root

        Assert.That(ForkChoiceRunner.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False,
            "a sidecar's inclusion proof against its OWN header proves nothing if that header is not this block");
    }

    [Test]
    public void MatchesBlock_rejects_a_commitment_count_mismatch()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        sidecar.KzgCommitments = [commitment, commitment]; // block has one commitment, this sidecar claims two

        Assert.That(ForkChoiceRunner.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False);
    }

    [Test]
    public void MatchesBlock_rejects_a_commitment_value_mismatch()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        byte[] wrongCommitment = new byte[SszKzgCommitment.KzgCommitmentLength];
        wrongCommitment[0] = 0xFF;
        sidecar.KzgCommitments = [SszKzgCommitment.FromSpan(wrongCommitment)];

        Assert.That(ForkChoiceRunner.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False,
            "a sidecar claiming a commitment the block never made must not count towards availability");
    }

    [Test]
    public void MatchesBlock_accepts_a_sidecar_that_genuinely_matches()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);

        Assert.That(ForkChoiceRunner.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.True,
            "a same-block, same-commitments sidecar must not be rejected by the addressing check itself");
    }

    private static BeaconBlock BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment)
    {
        commitment = SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength]);
        // TestChain.CreateBlock fills in every other required-for-merkleization field (execution
        // payload, sync aggregate, eth1 data, ...); only the commitments list is under test here.
        BeaconBlock block = TestChain.CreateBlock(slot: 5, parentRoot: Hash256.Zero).Message!;
        block.Body!.BlobKzgCommitments = [commitment];
        root = SszRoots.HashTreeRoot(block);
        return block;
    }

    /// <summary>
    /// A sidecar whose header round-trips to <paramref name="blockRoot"/> and whose commitments match the
    /// block's - everything <see cref="ForkChoiceRunner.IsDataAvailable"/> cross-checks before it would ever
    /// reach KZG verification. <c>hash_tree_root(header)</c> equals <paramref name="blockRoot"/> only because
    /// <c>BodyRoot</c> is the real <c>hash_tree_root(block.Body)</c>: header and block share the same first
    /// four fields, so the body is the only place a wrong root could hide.
    /// </summary>
    private static DataColumnSidecar MatchingSidecar(BeaconBlock block, Hash256 blockRoot, SszKzgCommitment commitment, ulong index) => new()
    {
        Index = index,
        KzgCommitments = [commitment],
        SignedBlockHeader = new SignedBeaconBlockHeader
        {
            Message = new BeaconBlockHeader
            {
                Slot = block.Slot,
                ProposerIndex = block.ProposerIndex,
                ParentRoot = block.ParentRoot,
                StateRoot = block.StateRoot,
                BodyRoot = SszRoots.HashTreeRoot(block.Body!),
            },
        },
    };
}
