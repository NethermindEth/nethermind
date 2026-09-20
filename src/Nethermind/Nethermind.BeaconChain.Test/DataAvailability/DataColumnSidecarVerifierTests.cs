// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DataColumnSidecarVerifierTests
{
    private const int ColumnIndex = 5;

    /// <summary>
    /// Builds a fully valid sidecar (real KZG cells/proofs for <paramref name="blobCount"/> blobs at
    /// <see cref="ColumnIndex"/>, plus a genuinely folded depth-4/index-11 inclusion proof), and
    /// returns the pieces needed to tamper with one aspect at a time.
    /// </summary>
    private static DataColumnSidecar BuildValidSidecar(int blobCount = 2)
    {
        DataColumnKzgFixture.BlobFixture[] blobs = [.. Enumerable.Range(0, blobCount).Select(i => DataColumnKzgFixture.BuildBlob((byte)(0x10 * (i + 1))))];

        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];
        SszBlobCell[] column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, ColumnIndex))];
        SszKzgCommitment[] proofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, ColumnIndex))];

        Hash256 leaf = DataColumnSidecarVerifier.ComputeCommitmentsListRoot(commitments);
        (Hash256[] branch, Hash256 bodyRoot) = FoldMerkleProof(leaf, seed: 0xB0);

        return new DataColumnSidecar
        {
            Index = ColumnIndex,
            Column = column,
            KzgCommitments = commitments,
            KzgProofs = proofs,
            SignedBlockHeader = new SignedBeaconBlockHeader
            {
                Message = new BeaconBlockHeader
                {
                    Slot = 1,
                    ProposerIndex = 0,
                    ParentRoot = new Hash256(Enumerable.Repeat((byte)0x01, 32).ToArray()),
                    StateRoot = new Hash256(Enumerable.Repeat((byte)0x02, 32).ToArray()),
                    BodyRoot = bodyRoot,
                },
                Signature = new BlsSignature(new byte[BlsSignature.Length]),
            },
            KzgCommitmentsInclusionProof = branch,
        };
    }

    /// <summary>
    /// Picks depth arbitrary-but-fixed sibling hashes and folds <paramref name="leaf"/> up to a root
    /// using the same algorithm <c>is_valid_merkle_branch</c> verifies (spec ssz merkle-proofs.md),
    /// reimplemented independently here rather than calling back into the verifier under test.
    /// </summary>
    private static (Hash256[] branch, Hash256 root) FoldMerkleProof(Hash256 leaf, byte seed)
    {
        Hash256[] branch = [.. Enumerable.Range(0, Eip7594DasConstants.KzgCommitmentsInclusionProofDepth)
            .Select(i => new Hash256(Enumerable.Repeat((byte)(seed + i), 32).ToArray()))];

        byte[] value = leaf.Bytes.ToArray();
        byte[] combined = new byte[64];
        for (int level = 0; level < branch.Length; level++)
        {
            byte[] sibling = branch[level].Bytes.ToArray();
            if (((Eip7594DasConstants.BlobKzgCommitmentsSubtreeIndex >> level) & 1) == 1)
            {
                sibling.CopyTo(combined, 0);
                value.CopyTo(combined, 32);
            }
            else
            {
                value.CopyTo(combined, 0);
                sibling.CopyTo(combined, 32);
            }
            value = SHA256.HashData(combined);
        }

        return (branch, new Hash256(value));
    }

    [Test]
    public void A_correctly_formed_sidecar_passes_every_check()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();

        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.True);
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.True);
            Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.True);
        });
    }

    [Test]
    public void A_sidecar_with_a_tampered_cell_fails_kzg_verification()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        byte[] tampered = sidecar.Column![0].AsSpan().ToArray();
        tampered[0] ^= 0xFF;
        sidecar.Column[0] = SszBlobCell.FromSpan(tampered);

        // The tamper does not change any array length, so structure still looks fine; only the
        // cryptographic check must catch it.
        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.True);
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
        });
    }

    [Test]
    public void A_sidecar_with_a_tampered_proof_fails_kzg_verification()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        byte[] tampered = sidecar.KzgProofs![0].AsSpan().ToArray();
        tampered[0] ^= 0xFF;
        sidecar.KzgProofs[0] = SszKzgCommitment.FromSpan(tampered);

        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
    }

    [Test]
    public void A_sidecar_whose_commitments_were_never_included_fails_inclusion_proof_even_though_its_cells_verify()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();

        // Swap in a wholly different (but internally self-consistent) commitment set built from a
        // fresh blob, without recomputing the inclusion proof: this is exactly the attack the
        // inclusion-proof check exists for - cells that verify, but were never in the claimed block.
        DataColumnKzgFixture.BlobFixture unrelatedBlob = DataColumnKzgFixture.BuildBlob(0xEE);
        sidecar.KzgCommitments = [DataColumnKzgFixture.CommitmentOf(unrelatedBlob), sidecar.KzgCommitments![1]];
        sidecar.Column![0] = DataColumnKzgFixture.CellAt(unrelatedBlob, ColumnIndex);
        sidecar.KzgProofs![0] = DataColumnKzgFixture.ProofAt(unrelatedBlob, ColumnIndex);

        Assert.Multiple(() =>
        {
            // Its own cells are still internally consistent with its own (now-swapped) commitments.
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.True);
            // But the swapped commitments list's root no longer matches the stale inclusion proof.
            Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.False);
        });
    }

    [Test]
    public void A_sidecar_with_a_tampered_inclusion_proof_fails()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        byte[] tamperedSibling = sidecar.KzgCommitmentsInclusionProof![0].Bytes.ToArray();
        tamperedSibling[0] ^= 0xFF;
        sidecar.KzgCommitmentsInclusionProof[0] = new Hash256(tamperedSibling);

        Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.False);
    }

    [Test]
    public void VerifyStructure_rejects_an_out_of_range_index()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.Index = (ulong)Eip7594DasConstants.NumberOfColumns;

        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
    }

    [Test]
    public void VerifyStructure_rejects_zero_commitments()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.KzgCommitments = [];
        sidecar.Column = [];
        sidecar.KzgProofs = [];

        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
    }

    [Test]
    public void VerifyStructure_rejects_a_column_proofs_length_mismatch()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.KzgProofs = [sidecar.KzgProofs![0]];

        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
    }

    [Test]
    public void VerifyKzgProofs_rejects_a_structurally_invalid_sidecar_without_indexing_past_its_arrays()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        // A hostile peer can send mismatched lengths; the batch loop indexes all three arrays in
        // lockstep off the commitment count, so this must be refused rather than indexed.
        sidecar.KzgProofs = [sidecar.KzgProofs![0]];

        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
            Assert.That(DataColumnSidecarVerifier.Verify(sidecar, BeaconChainSpec.Mainnet, BeaconChainSpec.Mainnet.FuluForkEpoch), Is.False);
        });
    }

    [Test]
    public void VerifyBlobCount_rejects_a_sidecar_with_no_commitments()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.KzgCommitments = [];

        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, BeaconChainSpec.Mainnet, BeaconChainSpec.Mainnet.FuluForkEpoch), Is.False);
    }

    [Test]
    public void VerifyBlobCount_rejects_a_sidecar_with_null_commitments()
    {
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.KzgCommitments = null;

        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, BeaconChainSpec.Mainnet, BeaconChainSpec.Mainnet.FuluForkEpoch), Is.False);
    }

    /// <summary>
    /// max_blobs_per_block is not a constant: BPO forks raise it at a scheduled epoch without a fork
    /// version bump. A sidecar with a commitment count that a stale, fixed bound would have accepted
    /// must still be judged against the schedule live at its own claimed epoch.
    /// </summary>
    [Test]
    public void VerifyBlobCount_enforces_the_schedule_live_at_the_claimed_epoch_not_a_fixed_maximum()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        // Mainnet's own BPO1 (412672, max 15): 10 commitments is within the pre-BPO Electra/Fulu bound (9)
        // only at BPO1 and later, never before.
        DataColumnSidecar sidecar = BuildValidSidecar();
        sidecar.KzgCommitments = new SszKzgCommitment[10];

        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, mainnet, mainnet.FuluForkEpoch), Is.False,
                "before any BPO fork, Fulu still inherits Electra's max_blobs_per_block of 9");
            Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, mainnet, 412672 - 1), Is.False,
                "the epoch immediately before BPO1 activates must still use the pre-BPO bound of 9");
            Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, mainnet, 412672), Is.True,
                "BPO1's own activation epoch raises the bound to 15, admitting 10 commitments");
        });
    }

    [Test]
    public void VerifyBlobCount_accepts_exactly_the_scheduled_maximum_and_rejects_one_more()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        DataColumnSidecar atMax = BuildValidSidecar();
        atMax.KzgCommitments = new SszKzgCommitment[9];
        DataColumnSidecar overMax = BuildValidSidecar();
        overMax.KzgCommitments = new SszKzgCommitment[10];

        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(atMax, mainnet, mainnet.FuluForkEpoch), Is.True);
            Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(overMax, mainnet, mainnet.FuluForkEpoch), Is.False);
        });
    }

    private static BeaconChainSpec SmallBoundedBlobSpec() => new()
    {
        ChainId = 0,
        SecondsPerSlot = 12,
        SlotsPerEpoch = 32,
        GenesisTime = 0,
        GenesisValidatorsRoot = Hash256.Zero,
        Forks = [new(Bytes.FromHexString("0x00000000"), 0)],
        BlobSchedule = [new(20, 2)], // BPO raises the bound from 1 to 2 at epoch 20
        ElectraForkEpoch = 0,
        FuluForkEpoch = 10,
        MaxBlobsPerBlockElectra = 1,
        GloasForkEpoch = Presets.FarFutureEpoch,
        GloasForkVersion = Bytes.FromHexString("0x00000000"),
    };

    /// <summary>
    /// End-to-end through <see cref="DataColumnSidecarVerifier.Verify"/> with a cryptographically real
    /// sidecar (not just a commitment-count fixture), crossing an actual BPO boundary: the same sidecar
    /// is rejected before the bound rises and accepted at the epoch it does.
    /// </summary>
    [Test]
    public void Verify_rejects_a_sidecar_over_the_bound_at_its_claimed_epoch_and_accepts_it_once_the_bound_rises()
    {
        BeaconChainSpec spec = SmallBoundedBlobSpec();
        DataColumnSidecar sidecar = BuildValidSidecar(blobCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(DataColumnSidecarVerifier.Verify(sidecar, spec, 19), Is.False,
                "one epoch before the BPO, the schedule still caps max_blobs_per_block at 1");
            Assert.That(DataColumnSidecarVerifier.Verify(sidecar, spec, 20), Is.True,
                "at the BPO's own activation epoch the cap rises to 2, admitting this sidecar");
        });
    }
}
