// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/forkchoice</c>, the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV{N}</c>.
/// Routes through <see cref="IEngineRpcModule"/> so that single-flight
/// locking, metrics, and the engine request tracker are applied identically
/// to the JSON-RPC path.
/// </summary>
public sealed class ForkchoiceUpdatedSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "forkchoice";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> call =
            version is > EngineApiVersions.Fcu.V4 or < EngineApiVersions.Fcu.V1
                ? FailToResolveVersion(version)
                : Call(version, body);

        await WriteSszResultAsync(ctx, await call, SszCodec.EncodeForkchoiceUpdatedResponse);
    }

    private Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(int version, ReadOnlyMemory<byte> body)
    {
        (ForkchoiceStateV1 state, PayloadAttributes? attrs) =
            SszCodec.DecodeForkchoiceUpdatedRequest(body.Span, version);

        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> call = version switch
        {
            EngineApiVersions.Fcu.V1 => engineModule.engine_forkchoiceUpdatedV1(state, attrs),
            EngineApiVersions.Fcu.V2 => engineModule.engine_forkchoiceUpdatedV2(state, attrs),
            EngineApiVersions.Fcu.V3 => engineModule.engine_forkchoiceUpdatedV3(state, attrs),
            EngineApiVersions.Fcu.V4 => engineModule.engine_forkchoiceUpdatedV4(state, attrs),
            _ => FailToResolveVersion(version)
        };

        return call;
    }

    private static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> FailToResolveVersion(int version) =>
        Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(
            $"Unsupported engine_forkchoiceUpdated version: {version}", ErrorCodes.MethodNotFound));
}
