// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The <c>engine_getPayloadV7</c> response: the V6 response plus the build's EIP-7805 omission claims.</summary>
public class GetPayloadV7Result(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV6Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder)
{
    /// <summary>An omission claim for each admitted Profile 2 candidate the build found invalid where it tried to
    /// append it, at most <see cref="Eip8369Constants.MaxOmissionClaims"/>.</summary>
    public OmissionClaim[] OmissionClaims => Block.OmissionClaims ?? [];
}

public sealed class GetPayloadV7DirectResponse(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV7Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder), IStreamableResult
{
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        GetPayloadDirectResponseWriter.WriteV7Async(writer, Block, BlockValue, BlobsBundle, ExecutionRequests, ShouldOverrideBuilder, OmissionClaims, cancellationToken);
}
