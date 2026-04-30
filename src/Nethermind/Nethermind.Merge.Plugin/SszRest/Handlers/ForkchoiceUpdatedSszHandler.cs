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
    private readonly IEngineRpcModule _engineModule = engineModule;

    public override string HttpMethod => "POST";
    public override string Resource => "forkchoice";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        if (version > EngineApiVersions.Fcu.Latest)
        {
            await WriteSszResultAsync(
                ctx,
                ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(
                    $"Unsupported engine_forkchoiceUpdated version: {version}",
                    ErrorCodes.MethodNotFound),
                (ForkchoiceUpdatedV1Result r) => SszCodec.EncodeForkchoiceUpdatedResponse(r));
            return;
        }

        (ForkchoiceStateV1 state, PayloadAttributes? attrs) =
            SszCodec.DecodeForkchoiceUpdatedRequest(body.Span, version);

        await WriteSszResultAsync(
            ctx,
            await SszCodec.DispatchForkchoiceUpdatedCall(_engineModule, version, state, attrs),
            (ForkchoiceUpdatedV1Result r) => SszCodec.EncodeForkchoiceUpdatedResponse(r));
    }
}
