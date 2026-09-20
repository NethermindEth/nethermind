// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// The supernode <c>is_data_available</c> the consensus-spec fork choice vectors are written
/// against, and the per-sidecar predicates under it. The predicates are tested directly as well as
/// through the rule because <see cref="DataColumnSidecarVerifier.Verify"/> always fails on a
/// hand-built sidecar (no real KZG proof), which would otherwise mask a missing or broken
/// count/duplicate/addressing check behind Verify's own unrelated failure.
/// </summary>
public class FullColumnSetAvailabilityTests
{
    [Test]
    public void Trivially_true_for_a_block_with_no_blob_commitments()
    {
        BeaconBlock block = TestChain.CreateBlock(slot: 1, parentRoot: Hash256.Zero).Message!;
        Hash256 root = SszRoots.HashTreeRoot(block);

        Assert.That(new FullColumnSetAvailability(null).IsDataAvailable(block, root, BeaconChainSpec.Mainnet), Is.True);
    }

    [Test]
    public void Rejects_a_block_with_commitments_and_no_columns()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out _);

        Assert.That(new FullColumnSetAvailability(null).IsDataAvailable(block, root, BeaconChainSpec.Mainnet), Is.False);
    }

    [Test]
    public void Rejects_fewer_than_the_full_column_set()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar[] columns = [MatchingSidecar(block, root, commitment, index: 0)];

        Assert.That(new FullColumnSetAvailability(columns).IsDataAvailable(block, root, BeaconChainSpec.Mainnet), Is.False,
            "one column out of NumberOfColumns must not count as available");
    }

    [Test]
    public void Accepts_the_full_verified_matrix_and_rejects_it_with_one_column_removed()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconBlock block = chain.Block.Message!;
        DataColumnSidecar[] allButOne = [.. chain.Columns.Where(c => c.Index != 77)];

        Assert.Multiple(() =>
        {
            Assert.That(new FullColumnSetAvailability(chain.Columns).IsDataAvailable(block, chain.BlockRoot, chain.Spec), Is.True,
                "a real, fully verifying 128-column matrix is the one thing this rule accepts");
            Assert.That(new FullColumnSetAvailability(allButOne).IsDataAvailable(block, chain.BlockRoot, chain.Spec), Is.False,
                "127 verified columns are still not the supernode's full set");
        });
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_a_duplicate_index()
    {
        DataColumnSidecar[] columns = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new DataColumnSidecar { Index = 0 }; // every sidecar claims index 0

        Assert.That(DataColumnAvailability.HasExactlyOneSidecarPerColumn(columns), Is.False,
            "128 sidecars all claiming column 0 must not be treated as the full 128-column set");
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_an_index_out_of_range()
    {
        DataColumnSidecar[] columns = FullIndexSet();
        columns[0].Index = (ulong)Eip7594DasConstants.NumberOfColumns; // one past the valid range

        Assert.That(DataColumnAvailability.HasExactlyOneSidecarPerColumn(columns), Is.False);
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_too_few_sidecars() =>
        Assert.That(DataColumnAvailability.HasExactlyOneSidecarPerColumn([new DataColumnSidecar { Index = 0 }]), Is.False);

    [Test]
    public void HasExactlyOneSidecarPerColumn_accepts_the_full_set_exactly_once_each() =>
        Assert.That(DataColumnAvailability.HasExactlyOneSidecarPerColumn(FullIndexSet()), Is.True);

    private static DataColumnSidecar[] FullIndexSet()
    {
        DataColumnSidecar[] columns = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = new DataColumnSidecar { Index = (ulong)i };
        return columns;
    }

    [Test]
    public void MatchesBlock_rejects_a_sidecar_addressed_to_a_different_block()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        sidecar.SignedBlockHeader!.Message!.Slot = 999; // changes the header's own hash tree root

        Assert.That(DataColumnAvailability.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False,
            "a sidecar's inclusion proof against its OWN header proves nothing if that header is not this block");
    }

    [Test]
    public void MatchesBlock_rejects_a_commitment_count_mismatch()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        sidecar.KzgCommitments = [commitment, commitment]; // block has one commitment, this sidecar claims two

        Assert.That(DataColumnAvailability.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False);
    }

    [Test]
    public void MatchesBlock_rejects_a_commitment_value_mismatch()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        byte[] wrongCommitment = new byte[SszKzgCommitment.KzgCommitmentLength];
        wrongCommitment[0] = 0xFF;
        sidecar.KzgCommitments = [SszKzgCommitment.FromSpan(wrongCommitment)];

        Assert.That(DataColumnAvailability.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.False,
            "a sidecar claiming a commitment the block never made must not count towards availability");
    }

    [Test]
    public void MatchesBlock_accepts_a_sidecar_that_genuinely_matches()
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);

        Assert.That(DataColumnAvailability.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.True,
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
    /// block's - everything <see cref="DataColumnAvailability.MatchesBlock"/> cross-checks before a rule would
    /// ever reach KZG verification. <c>hash_tree_root(header)</c> equals <paramref name="blockRoot"/> only because
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
