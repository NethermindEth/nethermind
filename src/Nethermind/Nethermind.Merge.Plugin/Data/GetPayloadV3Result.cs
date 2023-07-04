// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

public class GetPayloadV3Result : GetPayloadV2Result
{
    public GetPayloadV3Result(Block block, UInt256 blockFees, BlobsBundleV1 blobsBundle) : base(block, blockFees)
    {
        BlobsBundle = blobsBundle;
    }

    public BlobsBundleV1 BlobsBundle { get; }

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}}}";
}
