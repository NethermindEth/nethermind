// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.DataAvailability;

// Check predicates separately: synthetic sidecars lack real KZG proofs, which would otherwise mask count/address/duplicate defects.
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

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(new FullColumnSetAvailability(chain.Columns).IsDataAvailable(block, chain.BlockRoot, chain.Spec), Is.True,
            "a real, fully verifying 128-column matrix is the one thing this rule accepts");
        Assert.That(new FullColumnSetAvailability(allButOne).IsDataAvailable(block, chain.BlockRoot, chain.Spec), Is.False,
            "127 verified columns are still not the supernode's full set");
    }

    [Test]
    public void HasExactlyOneSidecarPerColumn_rejects_a_duplicate_index()
    {
        DataColumnSidecar[] columns = Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns)
            .Select(static _ => new DataColumnSidecar { Index = 0 }).ToArray();

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

    private static DataColumnSidecar[] FullIndexSet() => Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns)
        .Select(static i => new DataColumnSidecar { Index = (ulong)i }).ToArray();

    private static IEnumerable<TestCaseData> BlockMatches()
    {
        yield return Case("MatchesBlock_rejects_a_sidecar_addressed_to_a_different_block", s => s.SignedBlockHeader!.Message!.Slot = 999, false,
            "a sidecar's inclusion proof against its OWN header proves nothing if that header is not this block");
        yield return Case("MatchesBlock_rejects_a_commitment_count_mismatch", s => s.KzgCommitments = [s.KzgCommitments![0], s.KzgCommitments[0]], false);
        yield return Case("MatchesBlock_rejects_a_commitment_value_mismatch", s =>
        {
            byte[] wrongCommitment = new byte[SszKzgCommitment.KzgCommitmentLength];
            wrongCommitment[0] = 0xFF;
            s.KzgCommitments = [SszKzgCommitment.FromSpan(wrongCommitment)];
        }, false, "a sidecar claiming a commitment the block never made must not count towards availability");
        yield return Case("MatchesBlock_accepts_a_sidecar_that_genuinely_matches", _ => { }, true,
            "a same-block, same-commitments sidecar must not be rejected by the addressing check itself");

        static TestCaseData Case(string name, Action<DataColumnSidecar> arrange, bool expected, string? message = null) =>
            new TestCaseData(arrange, expected, message).SetName(name);
    }

    [TestCaseSource(nameof(BlockMatches))]
    public void MatchesBlock_checks_address_and_commitments(Action<DataColumnSidecar> arrange, bool expected, string? message)
    {
        BeaconBlock block = BlockWithOneCommitment(out Hash256 root, out SszKzgCommitment commitment);
        DataColumnSidecar sidecar = MatchingSidecar(block, root, commitment, index: 0);
        arrange(sidecar);

        Assert.That(DataColumnAvailability.MatchesBlock(sidecar, root, block.Body!.BlobKzgCommitments!), Is.EqualTo(expected), message);
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
