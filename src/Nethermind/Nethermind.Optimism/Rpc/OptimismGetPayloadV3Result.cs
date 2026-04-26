// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.Rpc;

public class OptimismGetPayloadV3Result(GetPayloadV3Result<ExecutionPayloadV3> result)
{
    public UInt256 BlockValue { get; } = result.BlockValue;
    public ExecutionPayloadV3 ExecutionPayload { get; } = result.ExecutionPayload;

    public BlobsBundleV1 BlobsBundle { get; } = result.BlobsBundle;

    public Hash256 ParentBeaconBlockRoot { get; set; } = result.ExecutionPayload.ParentBeaconBlockRoot!;

    public bool ShouldOverrideBuilder { get; } = result.ShouldOverrideBuilder;

    public override string ToString() => $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ParentBeaconBlockRoot: {ParentBeaconBlockRoot}, ShouldOverrideBuilder {ShouldOverrideBuilder}}}";
}
