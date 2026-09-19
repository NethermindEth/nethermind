// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Fulu <c>DataColumnSidecar</c> (das-core.md). Field order and types match the spec exactly; a
/// mismatch here changes the hash tree root and silently forks the node.
/// </summary>
/// <remarks>
/// Gloas (ePBS) drops <see cref="SignedBlockHeader"/> and <see cref="KzgCommitmentsInclusionProof"/>
/// from this container, since commitment authentication moves to the builder's bid instead of the
/// beacon block header. This type is the Fulu shape only; do not extend it to also cover Gloas.
/// </remarks>
[SszContainer]
public partial class DataColumnSidecar
{
    /// <summary>The column index in the extended data matrix, in <c>[0, NUMBER_OF_COLUMNS)</c>.</summary>
    public ulong Index { get; set; }

    /// <summary>One cell per blob in the block, at <see cref="Index"/>'s column position.</summary>
    [SszList(Eip7594DasConstants.MaxBlobCommitmentsPerBlock)]
    public SszBlobCell[]? Column { get; set; }

    /// <summary>One KZG commitment per blob in the block.</summary>
    [SszList(Eip7594DasConstants.MaxBlobCommitmentsPerBlock)]
    public SszKzgCommitment[]? KzgCommitments { get; set; }

    /// <summary>One cell KZG proof per blob, for this column. <c>KZGProof</c> is also a 48-byte
    /// ByteVector, so it reuses <see cref="SszKzgCommitment"/>'s wire/merkleization shape.</summary>
    [SszList(Eip7594DasConstants.MaxBlobCommitmentsPerBlock)]
    public SszKzgCommitment[]? KzgProofs { get; set; }

    public SignedBeaconBlockHeader? SignedBlockHeader { get; set; }

    /// <summary>
    /// Merkle branch proving <c>hash_tree_root(kzg_commitments)</c> is the block's
    /// <c>blob_kzg_commitments</c> field, against <c>signed_block_header.message.body_root</c>.
    /// </summary>
    [SszVector(Eip7594DasConstants.KzgCommitmentsInclusionProofDepth)]
    public Hash256[]? KzgCommitmentsInclusionProof { get; set; }
}
