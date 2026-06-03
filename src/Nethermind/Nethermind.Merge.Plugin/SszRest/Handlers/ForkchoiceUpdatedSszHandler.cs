// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/forkchoice</c>, the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV{N}</c>. Generic over a per-version descriptor
/// so adding V5 is one new descriptor struct + one DI line — no version switch.
/// </summary>
public sealed class ForkchoiceUpdatedSszHandler<TVersion, TWire>(IEngineRpcModule engineModule, ISpecProvider specProvider) : SszEndpointHandlerBase
    where TVersion : struct, IForkchoiceUpdatedVersion<TWire>
    where TWire : struct, ISszCodec<TWire>
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Forkchoice;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        TWire.Decode(body, out TWire wire);

        ulong? timestamp = TVersion.GetTimestamp(wire);
        if (timestamp.HasValue)
        {
            if (ctx.Items.TryGetValue("SszRouteFork", out object? forkObj) && forkObj is string urlFork)
            {
                IReleaseSpec timestampSpec = specProvider.GetSpec(ForkActivation.TimestampOnly(timestamp.Value));
                if (!timestampSpec.Name.Equals(urlFork, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Unsupported fork", MergeErrorCodes.UnsupportedFork);
                    return;
                }
            }
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await TVersion.Call(engineModule, wire);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeForkchoiceUpdatedResponse);
    }
}
