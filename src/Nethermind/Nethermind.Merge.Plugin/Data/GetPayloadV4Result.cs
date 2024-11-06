// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV4Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle, byte[][] executionRequests) : GetPayloadV3Result<ExecutionPayloadV3>(block, blockFees, blobsBundle)
{
    public byte[][]? ExecutionRequests { get; } = executionRequests;

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";
}
