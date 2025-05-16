// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV3Result<TVersionedExecutionPayload>(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle)
    : GetPayloadV2Result<TVersionedExecutionPayload>(block, blockFees)
    where TVersionedExecutionPayload : ExecutionPayloadV3, IExecutionPayloadParams, IExecutionPayloadFactory<TVersionedExecutionPayload>
{
    public BlobsBundleV1 BlobsBundle { get; } = blobsBundle;

    public override bool ValidateFork(ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(ExecutionPayload.BlockNumber, ExecutionPayload.Timestamp));
        return spec.IsEip4844Enabled && !spec.IsEip7623Enabled;
    }

    public bool ShouldOverrideBuilder { get; init; }
    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}}}";
}

public class GetPayloadV3Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle) : GetPayloadV3Result<ExecutionPayloadV3>(block, blockFees, blobsBundle);
