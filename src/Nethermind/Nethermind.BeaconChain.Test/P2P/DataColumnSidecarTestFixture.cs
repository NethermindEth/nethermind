// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Test.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Builds a fully valid <see cref="DataColumnSidecar"/> (real KZG cells/proofs and a genuinely
/// folded inclusion proof) for the P2P-layer tests, mirroring
/// <c>DataColumnSidecarVerifierTests.BuildValidSidecar</c> without depending on that test class.
/// </summary>
internal static class DataColumnSidecarTestFixture
{
    public static DataColumnSidecar BuildValidSidecar(ulong columnIndex, ulong slot = 1, ulong proposerIndex = 0, int blobCount = 2, byte seed = 0x10)
    {
        DataColumnKzgFixture.BlobFixture[] blobs = [.. Enumerable.Range(0, blobCount).Select(i => DataColumnKzgFixture.BuildBlob((byte)(seed * (i + 1))))];

        SszKzgCommitment[] commitments = [.. blobs.Select(DataColumnKzgFixture.CommitmentOf)];
        SszBlobCell[] column = [.. blobs.Select(b => DataColumnKzgFixture.CellAt(b, (int)columnIndex))];
        SszKzgCommitment[] proofs = [.. blobs.Select(b => DataColumnKzgFixture.ProofAt(b, (int)columnIndex))];

        Hash256 leaf = DataColumnSidecarVerifier.ComputeCommitmentsListRoot(commitments);
        (Hash256[] branch, Hash256 bodyRoot) = FoldMerkleProof(leaf, seed: 0xB0);

        return new DataColumnSidecar
        {
            Index = columnIndex,
            Column = column,
            KzgCommitments = commitments,
            KzgProofs = proofs,
            SignedBlockHeader = new SignedBeaconBlockHeader
            {
                Message = new BeaconBlockHeader
                {
                    Slot = slot,
                    ProposerIndex = proposerIndex,
                    ParentRoot = new Hash256(Enumerable.Repeat((byte)0x01, 32).ToArray()),
                    StateRoot = new Hash256(Enumerable.Repeat((byte)0x02, 32).ToArray()),
                    BodyRoot = bodyRoot,
                },
                Signature = new BlsSignature(new byte[BlsSignature.Length]),
            },
            KzgCommitmentsInclusionProof = branch,
        };
    }

    /// <summary>Folds <paramref name="leaf"/> up to a root using the same algorithm <c>is_valid_merkle_branch</c> verifies, independently of the verifier under test.</summary>
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
}
