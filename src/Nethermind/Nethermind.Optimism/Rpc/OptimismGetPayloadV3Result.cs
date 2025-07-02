// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.Rpc;

public class OptimismGetPayloadV3Result
{
    public OptimismGetPayloadV3Result(GetPayloadV3Result result)
    {
        ExecutionPayload = result.ExecutionPayload;
        BlockValue = result.BlockValue;

        BlobsBundle = result.BlobsBundle;
        ParentBeaconBlockRoot = result.ExecutionPayload.ParentBeaconBlockRoot!;
        ShouldOverrideBuilder = result.ShouldOverrideBuilder;
    }

    public UInt256 BlockValue { get; }
    public ExecutionPayloadV3 ExecutionPayload { get; }

    public BlobsBundleV1 BlobsBundle { get; }

    public Hash256 ParentBeaconBlockRoot { get; set; }

    public bool ShouldOverrideBuilder { get; }

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ParentBeaconBlockRoot: {ParentBeaconBlockRoot}, ShouldOverrideBuilder {ShouldOverrideBuilder}}}";
}
