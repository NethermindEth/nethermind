// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.DataAvailability;

// Round-trip/structural checks only; self-computed roots do not independently validate field order.
public class DataColumnSidecarContainerTests
{
    private static Hash256 Hash(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static DataColumnSidecar CreateRepresentative()
    {
        SszBlobCell cell0 = SszBlobCell.FromSpan(Enumerable.Repeat((byte)0x11, SszBlobCell.BlobCellLength).ToArray());
        SszBlobCell cell1 = SszBlobCell.FromSpan(Enumerable.Repeat((byte)0x22, SszBlobCell.BlobCellLength).ToArray());
        SszKzgCommitment commitment0 = SszKzgCommitment.FromSpan(Enumerable.Repeat((byte)0x33, SszKzgCommitment.KzgCommitmentLength).ToArray());
        SszKzgCommitment commitment1 = SszKzgCommitment.FromSpan(Enumerable.Repeat((byte)0x44, SszKzgCommitment.KzgCommitmentLength).ToArray());
        SszKzgCommitment proof0 = SszKzgCommitment.FromSpan(Enumerable.Repeat((byte)0x55, SszKzgCommitment.KzgCommitmentLength).ToArray());
        SszKzgCommitment proof1 = SszKzgCommitment.FromSpan(Enumerable.Repeat((byte)0x66, SszKzgCommitment.KzgCommitmentLength).ToArray());

        return new DataColumnSidecar
        {
            Index = 17,
            Column = [cell0, cell1],
            KzgCommitments = [commitment0, commitment1],
            KzgProofs = [proof0, proof1],
            SignedBlockHeader = new SignedBeaconBlockHeader
            {
                Message = new BeaconBlockHeader
                {
                    Slot = 100,
                    ProposerIndex = 7,
                    ParentRoot = Hash(0x01),
                    StateRoot = Hash(0x02),
                    BodyRoot = Hash(0x03),
                },
                Signature = new BlsSignature(Enumerable.Repeat((byte)0x77, BlsSignature.Length).ToArray()),
            },
            KzgCommitmentsInclusionProof = [Hash(0xA0), Hash(0xA1), Hash(0xA2), Hash(0xA3)],
        };
    }

    [Test]
    public void RefReader_reads_byte_structs_in_place()
    {
        DataColumnSidecar sidecar = CreateRepresentative();
        Validator validator = new() { Pubkey = new BlsPublicKey(Enumerable.Repeat((byte)0x88, BlsPublicKey.Length).ToArray()), WithdrawalCredentials = Hash(0xB0) };
        byte[] encodedSidecar = DataColumnSidecar.Encode(sidecar);
        byte[] encodedValidator = Validator.Encode(validator);

        DataColumnSidecar.RefReader sidecarReader = new(encodedSidecar);
        ReadOnlySpan<byte> cell = sidecarReader.Column[1].AsSpan();
        ReadOnlySpan<byte> proof = sidecarReader.KzgProofs[1].AsSpan();
        ReadOnlySpan<byte> signature = sidecarReader.SignedBlockHeader.Signature.Bytes;
        ReadOnlySpan<byte> pubkey = new Validator.RefReader(encodedValidator).Pubkey.Bytes;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(cell.ToArray(), Is.EqualTo(sidecar.Column![1].AsSpan().ToArray()));
        Assert.That(cell.Overlaps(encodedSidecar), Is.True, "cell is not copied");
        Assert.That(proof.ToArray(), Is.EqualTo(sidecar.KzgProofs![1].AsSpan().ToArray()));
        Assert.That(proof.Overlaps(encodedSidecar), Is.True, "proof is not copied");
        Assert.That(signature.ToArray(), Is.EqualTo(sidecar.SignedBlockHeader!.Signature.Bytes.ToArray()));
        Assert.That(signature.Overlaps(encodedSidecar), Is.True, "signature is not copied");
        Assert.That(pubkey.ToArray(), Is.EqualTo(validator.Pubkey.Bytes.ToArray()));
        Assert.That(pubkey.Overlaps(encodedValidator), Is.True, "pubkey is not copied");
    }

    [Test]
    public void Round_trips_through_ssz_encode_decode_with_a_stable_root()
    {
        DataColumnSidecar original = CreateRepresentative();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        (DataColumnSidecar decoded, UInt256 originalRoot) = GloasContainerTests.AssertRoundTrips(original);
        Assert.That(originalRoot, Is.Not.EqualTo(UInt256.Zero));

        Assert.That(decoded.Index, Is.EqualTo(original.Index));
        Assert.That(decoded.Column!.Select(c => c.AsSpan().ToArray()),
            Is.EqualTo(original.Column!.Select(c => c.AsSpan().ToArray())));
        Assert.That(decoded.KzgCommitments!.Select(c => c.AsSpan().ToArray()),
            Is.EqualTo(original.KzgCommitments!.Select(c => c.AsSpan().ToArray())));
        Assert.That(decoded.KzgProofs!.Select(c => c.AsSpan().ToArray()),
            Is.EqualTo(original.KzgProofs!.Select(c => c.AsSpan().ToArray())));
        Assert.That(decoded.SignedBlockHeader!.Message!.Slot, Is.EqualTo(100ul));
        Assert.That(decoded.KzgCommitmentsInclusionProof, Has.Length.EqualTo(Eip7594DasConstants.KzgCommitmentsInclusionProofDepth));
    }

    [TestCase(false, TestName = "A_different_column_index_changes_the_root")]
    [TestCase(true, TestName = "A_different_cell_value_changes_the_root")]
    public void Changing_a_column_changes_the_root(bool changeCell)
    {
        DataColumnSidecar a = CreateRepresentative();
        DataColumnSidecar b = CreateRepresentative();
        if (changeCell)
            b.Column![0] = SszBlobCell.FromSpan(Enumerable.Repeat((byte)0x99, SszBlobCell.BlobCellLength).ToArray());
        else
            b.Index = a.Index + 1;

        DataColumnSidecar.Merkleize(a, out UInt256 rootA);
        DataColumnSidecar.Merkleize(b, out UInt256 rootB);

        Assert.That(rootB, Is.Not.EqualTo(rootA));
    }
}
