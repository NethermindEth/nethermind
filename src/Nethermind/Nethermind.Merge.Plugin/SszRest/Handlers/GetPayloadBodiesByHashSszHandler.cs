// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/bodies/hash</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByHashV{N}</c> (the version is selected by the
/// <c>Eth-Execution-Version</c> header). Generic over a per-version descriptor
/// so adding a Vn+1 endpoint is one new descriptor + one DI line.
/// </summary>
public sealed class GetPayloadBodiesByHashSszHandler<TVersion, TResult>(
    IEngineRpcModule engineModule,
    IBlockFinder blockFinder,
    ISpecProvider specProvider)
    : SszEndpointHandlerBase
    where TVersion : struct, IPayloadBodiesByHashVersion<TResult>
    where TResult : class
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.PayloadBodiesByHash;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        Hash256[] hashes = SszCodec.DecodeGetPayloadBodiesByHashRequest(body);
        if (hashes.Length > SszRestLimits.MaxBodiesRequest)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge,
                $"hash count {hashes.Length} exceeds the limit of {SszRestLimits.MaxBodiesRequest}",
                MergeErrorCodes.TooLargeRequest);
            return;
        }
        ResultWrapper<IReadOnlyList<TResult?>> result = await TVersion.Call(engineModule, hashes);
        if (result.Result.ResultType == ResultType.Success && result.Data is { Count: > 0 } data)
        {
            string? urlFork = ctx.Items.TryGetValue("SszRouteFork", out object? f) ? f as string : null;
            if (urlFork is not null)
            {
                TResult?[] filtered = BodiesForkFilter.FilterByHash(data, hashes, urlFork, blockFinder, specProvider);
                ResultWrapper<IReadOnlyList<TResult?>> wrapped = ResultWrapper<IReadOnlyList<TResult?>>.Success(filtered);
                wrapped.AddDisposable(result.Dispose);
                result = wrapped;
            }
        }
        await WriteSszResultAsync(ctx, result, TVersion.Encode);
    }
}
