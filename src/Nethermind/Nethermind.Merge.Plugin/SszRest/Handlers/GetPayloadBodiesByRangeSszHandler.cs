// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/bodies?from=N&amp;count=M</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByRangeV{N}</c> (the version is selected by the
/// <c>Eth-Execution-Version</c> header). Generic over a per-version descriptor
/// so adding a Vn+1 endpoint is one new descriptor + one DI line.
/// </summary>
public sealed class GetPayloadBodiesByRangeSszHandler<TVersion, TResult>(
    IEngineRpcModule engineModule,
    IBlockFinder blockFinder,
    ISpecProvider specProvider)
    : SszEndpointHandlerBase
    where TVersion : struct, IPayloadBodiesByRangeVersion<TResult>
    where TResult : class
{
    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.PayloadBodiesByRange;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        // body is empty for GET; parameters come from the query string.
        if (!QueryParams.TryReadUlong(ctx, "from", static n => true,
                "must be a non-negative integer block number", out ulong start, out Task? error))
        {
            await error;
            return;
        }
        if (!QueryParams.TryReadUlong(ctx, "count", static n => n > 0,
                "must be a positive integer", out ulong count, out error))
        {
            await error;
            return;
        }
        if (count > SszRestLimits.MaxBodiesRequest)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge,
                $"count {count} exceeds the limit of {SszRestLimits.MaxBodiesRequest}",
                MergeErrorCodes.TooLargeRequest);
            return;
        }
        ResultWrapper<IReadOnlyList<TResult?>> result = await TVersion.Call(engineModule, start, count);
        string? requestedFork = ctx.Items.TryGetValue(SszMiddleware.RouteForkItemKey, out object? f) ? f as string : null;
        if (requestedFork is not null && result.Result.ResultType == ResultType.Success && result.Data is { Count: > 0 } data)
        {
            TResult?[] filtered = BodiesForkFilter.FilterByRange(data, start, requestedFork, blockFinder, specProvider);
            ResultWrapper<IReadOnlyList<TResult?>> wrapped = ResultWrapper<IReadOnlyList<TResult?>>.Success(filtered);
            wrapped.AddDisposable(result.Dispose);
            result = wrapped;
        }
        await WriteSszResultAsync(ctx, result, TVersion.Encode);
    }
}
