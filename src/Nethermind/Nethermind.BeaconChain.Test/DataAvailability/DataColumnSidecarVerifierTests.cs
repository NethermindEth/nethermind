// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DataColumnSidecarVerifierTests
{
    private const int ColumnIndex = 5;

    [Test]
    public void A_correctly_formed_sidecar_passes_every_check()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.True);
        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.True);
        Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.True);
    }

    [Test]
    public void A_sidecar_with_a_tampered_cell_fails_kzg_verification()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        byte[] tampered = sidecar.Column![0].AsSpan().ToArray();
        tampered[0] ^= 0xFF;
        sidecar.Column[0] = SszBlobCell.FromSpan(tampered);

        // Array lengths stay valid; only the cryptographic check can detect this tamper.
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.True);
        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
    }

    [Test]
    public void A_sidecar_with_a_tampered_proof_fails_kzg_verification()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        byte[] tampered = sidecar.KzgProofs![0].AsSpan().ToArray();
        tampered[0] ^= 0xFF;
        sidecar.KzgProofs[0] = SszKzgCommitment.FromSpan(tampered);

        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
    }

    [Test]
    public void A_sidecar_whose_commitments_were_never_included_fails_inclusion_proof_even_though_its_cells_verify()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);

        // New cells/commitments verify internally, but the stale inclusion proof must reject them.
        DataColumnKzgFixture.BlobFixture unrelatedBlob = DataColumnKzgFixture.BuildBlob(0xEE);
        sidecar.KzgCommitments = [DataColumnKzgFixture.CommitmentOf(unrelatedBlob), sidecar.KzgCommitments![1]];
        sidecar.Column![0] = DataColumnKzgFixture.CellAt(unrelatedBlob, ColumnIndex);
        sidecar.KzgProofs![0] = DataColumnKzgFixture.ProofAt(unrelatedBlob, ColumnIndex);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.True);
        Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.False);
    }

    [Test]
    public void A_sidecar_with_a_tampered_inclusion_proof_fails()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        byte[] tamperedSibling = sidecar.KzgCommitmentsInclusionProof![0].Bytes.ToArray();
        tamperedSibling[0] ^= 0xFF;
        sidecar.KzgCommitmentsInclusionProof[0] = new Hash256(tamperedSibling);

        Assert.That(DataColumnSidecarVerifier.VerifyInclusionProof(sidecar), Is.False);
    }

    [Test]
    public void VerifyStructure_rejects_an_out_of_range_index()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        sidecar.Index = (ulong)Eip7594DasConstants.NumberOfColumns;

        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
    }

    [Test]
    public void VerifyStructure_rejects_zero_commitments()
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        sidecar.KzgCommitments = [];
        sidecar.Column = [];
        sidecar.KzgProofs = [];

        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
    }

    [TestCase(false, TestName = "VerifyStructure_rejects_a_column_proofs_length_mismatch")]
    [TestCase(true, TestName = "VerifyKzgProofs_rejects_a_structurally_invalid_sidecar_without_indexing_past_its_arrays")]
    public void Mismatched_proof_lengths_are_refused_before_indexing_the_arrays(bool verifyKzg)
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        // Malformed lengths must be refused before the KZG batch indexes arrays.
        sidecar.KzgProofs = [sidecar.KzgProofs![0]];

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyStructure(sidecar), Is.False);
        if (verifyKzg)
        {
            Assert.That(DataColumnSidecarVerifier.VerifyKzgProofs(sidecar), Is.False);
            Assert.That(DataColumnSidecarVerifier.Verify(sidecar, BeaconChainSpec.Mainnet), Is.False);
        }
    }

    [TestCase(false, TestName = "VerifyBlobCount_rejects_a_sidecar_with_no_commitments")]
    [TestCase(true, TestName = "VerifyBlobCount_rejects_a_sidecar_with_null_commitments")]
    public void VerifyBlobCount_requires_commitments(bool nullCommitments)
    {
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        sidecar.KzgCommitments = nullCommitments ? null : [];

        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(sidecar, BeaconChainSpec.Mainnet), Is.False);
    }

    [Test]
    public void VerifyBlobCount_enforces_the_schedule_live_at_the_claimed_epoch_not_a_fixed_maximum()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        // Mainnet's own BPO1 (412672, max 15): 10 commitments is within the pre-BPO Electra/Fulu bound (9)
        // only at BPO1 and later, never before.
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        sidecar.KzgCommitments = new SszKzgCommitment[10];

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(AtEpoch(sidecar, mainnet, mainnet.FuluForkEpoch), mainnet), Is.False,
            "before any BPO fork, Fulu still inherits Electra's max_blobs_per_block of 9");
        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(AtEpoch(sidecar, mainnet, 412672 - 1), mainnet), Is.False,
            "the epoch immediately before BPO1 activates must still use the pre-BPO bound of 9");
        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(AtEpoch(sidecar, mainnet, 412672), mainnet), Is.True,
            "BPO1's own activation epoch raises the bound to 15, admitting 10 commitments");
    }

    [Test]
    public void VerifyBlobCount_accepts_exactly_the_scheduled_maximum_and_rejects_one_more()
    {
        BeaconChainSpec mainnet = BeaconChainSpec.Mainnet;
        DataColumnSidecar atMax = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        atMax.KzgCommitments = new SszKzgCommitment[9];
        DataColumnSidecar overMax = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex);
        overMax.KzgCommitments = new SszKzgCommitment[10];

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(AtEpoch(atMax, mainnet, mainnet.FuluForkEpoch), mainnet), Is.True);
        Assert.That(DataColumnSidecarVerifier.VerifyBlobCount(AtEpoch(overMax, mainnet, mainnet.FuluForkEpoch), mainnet), Is.False);
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
        Bootnodes = [],
    };

    [Test]
    public void Verify_rejects_a_sidecar_over_the_bound_at_its_claimed_epoch_and_accepts_it_once_the_bound_rises()
    {
        BeaconChainSpec spec = SmallBoundedBlobSpec();
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(ColumnIndex, blobCount: 2);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(DataColumnSidecarVerifier.Verify(AtEpoch(sidecar, spec, 19), spec), Is.False,
            "one epoch before the BPO, the schedule still caps max_blobs_per_block at 1");
        Assert.That(DataColumnSidecarVerifier.Verify(AtEpoch(sidecar, spec, 20), spec), Is.True,
            "at the BPO's own activation epoch the cap rises to 2, admitting this sidecar");
    }

    private static DataColumnSidecar AtEpoch(DataColumnSidecar sidecar, BeaconChainSpec spec, ulong epoch)
    {
        sidecar.SignedBlockHeader!.Message!.Slot = epoch * spec.SlotsPerEpoch;
        return sidecar;
    }
}
