// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV5Result(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests) : GetPayloadV4Result(block, blockFees, null!, executionRequests)
{
    public new BlobsBundleV2 BlobsBundle { get; } = blobsBundle;

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";
}
