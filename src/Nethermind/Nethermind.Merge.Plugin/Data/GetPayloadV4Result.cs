// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV4Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder) : GetPayloadV3Result<ExecutionPayloadV3>(block, blockFees, blobsBundle, shouldOverrideBuilder)
{
    public byte[][]? ExecutionRequests { get; } = executionRequests;

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";

    public override bool ValidateFork(ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(new ForkActivation(ExecutionPayload.BlockNumber, ExecutionPayload.Timestamp));
        return spec.IsEip7623Enabled && !spec.IsEip7594Enabled;
    }
}
