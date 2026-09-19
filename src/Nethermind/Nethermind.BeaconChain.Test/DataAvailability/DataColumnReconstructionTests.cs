// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class DataColumnReconstructionTests
{
    private static (DataColumnSidecar[] fullMatrix, SszKzgCommitment[] commitments) BuildFullMatrix(int blobCount = 2)
    {
        DataColumnKzgFixture.BlobFixture[] blobs = [.. Enumerable.Range(0, blobCount).Select(i => DataColumnKzgFixture.BuildBlob((byte)(0x20 * (i + 1))))];
        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];

        SignedBeaconBlockHeader header = new()
        {
            Message = new BeaconBlockHeader
            {
                Slot = 42,
                ProposerIndex = 0,
                ParentRoot = new Hash256(Enumerable.Repeat((byte)0x09, 32).ToArray()),
                StateRoot = new Hash256(Enumerable.Repeat((byte)0x0A, 32).ToArray()),
                BodyRoot = new Hash256(Enumerable.Repeat((byte)0x0B, 32).ToArray()),
            },
            Signature = new BlsSignature(new byte[BlsSignature.Length]),
        };
        Hash256[] inclusionProof = [.. Enumerable.Range(0, Eip7594DasConstants.KzgCommitmentsInclusionProofDepth)
            .Select(i => new Hash256(Enumerable.Repeat((byte)(0xC0 + i), 32).ToArray()))];

        DataColumnSidecar[] matrix = new DataColumnSidecar[Eip7594DasConstants.NumberOfColumns];
        for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
        {
            matrix[c] = new DataColumnSidecar
            {
                Index = (ulong)c,
                Column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, c))],
                KzgCommitments = commitments,
                KzgProofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, c))],
                SignedBlockHeader = header,
                KzgCommitmentsInclusionProof = inclusionProof,
            };
        }

        return (matrix, commitments);
    }

    private static byte[] Flatten(DataColumnSidecar[] matrix, System.Func<DataColumnSidecar, SszBlobCell[]> selector) =>
        [.. matrix.SelectMany(s => selector(s).SelectMany(c => c.AsSpan().ToArray()))];

    [Test]
    public void Reconstruction_from_exactly_the_threshold_succeeds_and_round_trips_to_the_original_data()
    {
        (DataColumnSidecar[] fullMatrix, _) = BuildFullMatrix();
        DataColumnSidecar[] held = [.. fullMatrix.Take(Eip7594DasConstants.RequiredColumnsForReconstruction)];

        bool ok = DataColumnReconstruction.TryReconstruct(held, out DataColumnSidecar[] recoveredMatrix);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(recoveredMatrix, Has.Length.EqualTo(Eip7594DasConstants.NumberOfColumns));
            Assert.That(Flatten(recoveredMatrix, s => s.Column!), Is.EqualTo(Flatten(fullMatrix, s => s.Column!)),
                "recovered cells, at every column including those never held, must equal the originals bit-for-bit");

            for (int c = 0; c < Eip7594DasConstants.NumberOfColumns; c++)
            {
                Assert.That(recoveredMatrix[c].Index, Is.EqualTo((ulong)c));
                Assert.That(recoveredMatrix[c].KzgCommitments, Is.SameAs(fullMatrix[0].KzgCommitments));
            }
        });
    }

    [Test]
    public void Reconstruction_from_one_fewer_than_the_threshold_fails()
    {
        (DataColumnSidecar[] fullMatrix, _) = BuildFullMatrix();
        DataColumnSidecar[] held = [.. fullMatrix.Take(Eip7594DasConstants.RequiredColumnsForReconstruction - 1)];

        bool ok = DataColumnReconstruction.TryReconstruct(held, out DataColumnSidecar[] recoveredMatrix);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(recoveredMatrix, Is.Empty);
        });
    }

    [Test]
    public void Reconstruction_counts_duplicate_indices_only_once_towards_the_threshold()
    {
        (DataColumnSidecar[] fullMatrix, _) = BuildFullMatrix();
        // 63 distinct columns, the last one repeated: still only 63 distinct columns of evidence.
        DataColumnSidecar[] held = [.. fullMatrix.Take(Eip7594DasConstants.RequiredColumnsForReconstruction - 1), fullMatrix[Eip7594DasConstants.RequiredColumnsForReconstruction - 2]];

        bool ok = DataColumnReconstruction.TryReconstruct(held, out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void Reconstruction_works_from_an_arbitrary_non_prefix_subset_of_columns()
    {
        (DataColumnSidecar[] fullMatrix, _) = BuildFullMatrix();
        DataColumnSidecar[] held = [.. fullMatrix.Where((_, i) => i % 2 == 0)]; // every even column: exactly 64

        bool ok = DataColumnReconstruction.TryReconstruct(held, out DataColumnSidecar[] recoveredMatrix);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(Flatten(recoveredMatrix, s => s.Column!), Is.EqualTo(Flatten(fullMatrix, s => s.Column!)));
        });
    }

    [Test]
    public void Reconstruction_rejects_an_empty_input()
    {
        bool ok = DataColumnReconstruction.TryReconstruct([], out DataColumnSidecar[] recoveredMatrix);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(recoveredMatrix, Is.Empty);
        });
    }
}
