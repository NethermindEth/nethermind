// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV6Result(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV5Result<ExecutionPayloadV4>(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder)
{
    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";

    public override bool ValidateFork(ISpecProvider specProvider) => specProvider.GetSpec(ExecutionPayload.BlockNumber, ExecutionPayload.Timestamp).IsEip7928Enabled;
}
