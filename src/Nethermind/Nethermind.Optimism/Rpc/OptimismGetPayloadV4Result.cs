// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.Rpc;

public class OptimismGetPayloadV4Result(GetPayloadV4Result result, Hash256? withdrawalsRoot) : OptimismGetPayloadV3Result(result, withdrawalsRoot)
{
    public byte[][]? ExecutionRequests { get; } = result.ExecutionRequests;

    public override string ToString() =>
        $"{{ExecutionPayload: {ExecutionPayload}, Fees: {BlockValue}, BlobsBundle blobs count: {BlobsBundle.Blobs.Length}, ShouldOverrideBuilder {ShouldOverrideBuilder}, ExecutionRequests count : {ExecutionRequests?.Length}}}";
}
