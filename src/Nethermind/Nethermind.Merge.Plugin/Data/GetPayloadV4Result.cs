// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV4Result : GetPayloadV2Result
{
    public GetPayloadV4Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle) : base(block, blockFees)
    {
        ExecutionPayload = new(block);
        BlobsBundle = blobsBundle;
    }

    public BlobsBundleV1 BlobsBundle { get; }

    public override ExecutionPayloadV4 ExecutionPayload { get; }

    public bool ShouldOverrideBuilder { get; }

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}}}";
}
