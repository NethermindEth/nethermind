// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.Rpc;

public class OptimismGetPayloadV4Result(GetPayloadV4Result result) : OptimismGetPayloadV3Result(result)
{
    public byte[][]? ExecutionRequests { get; } = result.ExecutionRequests;

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";
}
