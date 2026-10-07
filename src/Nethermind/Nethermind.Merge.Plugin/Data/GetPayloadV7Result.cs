// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The <c>engine_getPayloadV7</c> response: the V6 response plus the builder's EIP-8369 claims.</summary>
public class GetPayloadV7Result(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV6Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder)
{
    /// <summary>The claimed evaluation index of every omitted Profile 2 candidate the build tried to append.</summary>
    public InclusionListClaim[] InclusionListClaims => Block.InclusionListClaims ?? [];

    public override bool ValidateFork(ISpecProvider specProvider) => specProvider.GetSpec(Block.Number, Block.Timestamp).IsEip7805Enabled;
}

public sealed class GetPayloadV7DirectResponse(Block block, UInt256 blockFees, BlobsBundleV2 blobsBundle, byte[][] executionRequests, bool shouldOverrideBuilder)
    : GetPayloadV7Result(block, blockFees, blobsBundle, executionRequests, shouldOverrideBuilder), IStreamableResult
{
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        GetPayloadDirectResponseWriter.WriteV7Async(writer, Block, BlockValue, BlobsBundle, ExecutionRequests, ShouldOverrideBuilder, InclusionListClaims, cancellationToken);
}
