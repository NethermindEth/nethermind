// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Beacon API Fulu <c>BlockContents</c>: the SSZ body of <c>produceBlockV3</c>.</summary>
[SszContainer]
internal partial class BlockContentsWire
{
    /// <summary>beacon-APIs types/fulu/block_contents.yaml <c>KZGProofs</c>: <c>FIELD_ELEMENTS_PER_EXT_BLOB * MAX_BLOB_COMMITMENTS_PER_BLOCK</c>.</summary>
    internal const ulong MaxKzgProofs = 8192UL * Eip7594DasConstants.MaxBlobCommitmentsPerBlock;

    public BeaconBlock? Block { get; set; }
    [SszList(MaxKzgProofs)]
    public SszKzgCommitment[]? KzgProofs { get; set; }
    [SszList(Eip7594DasConstants.MaxBlobCommitmentsPerBlock)]
    public SszBlob[]? Blobs { get; set; }
}

/// <summary>Beacon API Fulu <c>SignedBlockContents</c>: the SSZ body of <c>publishBlockV2</c>.</summary>
[SszContainer]
internal partial class SignedBlockContentsWire
{
    public SignedBeaconBlock? SignedBlock { get; set; }
    [SszList(BlockContentsWire.MaxKzgProofs)]
    public SszKzgCommitment[]? KzgProofs { get; set; }
    [SszList(Eip7594DasConstants.MaxBlobCommitmentsPerBlock)]
    public SszBlob[]? Blobs { get; set; }
}
