// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data;

public sealed class GetPayloadV5DirectResponse(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV5Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder), IStreamableResult
{
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        GetPayloadDirectResponseWriter.WriteV5Async(writer, Block, BlockValue, BlobsBundle, ExecutionRequests, ShouldOverrideBuilder, cancellationToken);
}
