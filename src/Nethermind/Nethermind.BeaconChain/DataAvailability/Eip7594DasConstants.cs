// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// EIP-7594 (PeerDAS) mainnet preset and config constants, Fulu fork.
/// </summary>
/// <remarks>
/// Values verified directly against ethereum/consensus-specs (fetched 2026-09-19):
/// <c>presets/mainnet/fulu.yaml</c> for <see cref="NumberOfColumns"/> and
/// <see cref="KzgCommitmentsInclusionProofDepth"/>, <c>configs/mainnet.yaml</c> for the rest.
/// </remarks>
public static class Eip7594DasConstants
{
    /// <summary>Number of custody groups available for nodes to custody. <c>2**7</c>.</summary>
    public const ulong NumberOfCustodyGroups = 128;

    /// <summary>Number of gossipsub subnets carrying data column sidecars. <c>2**7</c>.</summary>
    public const ulong DataColumnSidecarSubnetCount = 128;

    /// <summary>Minimum number of custody groups an honest, non-supernode node custodies and serves samples from.</summary>
    public const ulong CustodyRequirement = 4;

    /// <summary>Minimum number of samples an honest node downloads and verifies per slot.</summary>
    public const ulong SamplesPerSlot = 8;

    /// <summary>Columns in the extended data matrix; equal to <c>CELLS_PER_EXT_BLOB</c>.</summary>
    public const int NumberOfColumns = 128;

    /// <summary>Epoch window a node MUST be able to serve DataColumnSidecarsByRange/ByRoot requests for. <c>2**12</c>.</summary>
    public const ulong MinEpochsForDataColumnSidecarsRequests = 4096;

    /// <summary>
    /// Merkle proof depth for <c>DataColumnSidecar.kzg_commitments_inclusion_proof</c>: the whole
    /// <c>blob_kzg_commitments</c> list is proven as a single field of <c>BeaconBlockBody</c>, not a
    /// per-commitment leaf (that is the pre-Fulu <c>BlobSidecar</c> shape, whose equivalent constant is 17).
    /// </summary>
    /// <remarks>
    /// <c>floorlog2(get_generalized_index(BeaconBlockBody, 'blob_kzg_commitments'))</c>. BeaconBlockBody has
    /// 13 fields (Electra/Fulu), so the container merkleizes over a 16-leaf (2**4) padded tree; the field's
    /// generalized index is 16 + 11 = 27, and floorlog2(27) = 4. Confirmed verbatim in the fetched
    /// <c>presets/mainnet/fulu.yaml</c> comment ("(= 4)").
    /// </remarks>
    public const int KzgCommitmentsInclusionProofDepth = 4;

    /// <summary>
    /// The subtree index of <c>blob_kzg_commitments</c> within the depth-<see cref="KzgCommitmentsInclusionProofDepth"/>
    /// merkle proof: <c>get_subtree_index(27) = 27 % 2**(27.bit_length()-1) = 27 % 16 = 11</c>, i.e. the field's
    /// zero-based position among BeaconBlockBody's 13 fields (randao_reveal=0 .. execution_requests=12).
    /// </summary>
    public const int BlobKzgCommitmentsSubtreeIndex = 11;

    /// <summary>List/vector limit for KZG commitments, KZG proofs, and column cells per block.</summary>
    public const int MaxBlobCommitmentsPerBlock = 4096;

    /// <summary>
    /// Minimum distinct columns needed to reconstruct the full matrix: "If the node obtains 50%+ of
    /// all the columns, it SHOULD reconstruct the full data matrix" (das-core.md). Same 50% threshold
    /// as the execution side's <c>BlobCellsHelper.RequiredCellsForRecovery</c>, at column granularity.
    /// </summary>
    public const int RequiredColumnsForReconstruction = NumberOfColumns / 2;
}
