// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/client/version</c>, the SSZ-REST equivalent of
/// <c>engine_getClientVersionV1</c>.
/// </summary>
public sealed class ClientVersionSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    private readonly IEngineRpcModule _engineModule = engineModule;

    public override string HttpMethod => "POST";
    public override string Resource => "client/version";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        ClientVersionV1 callerVersion = SszCodec.DecodeClientVersionRequest(body.Span);

        ResultWrapper<ClientVersionV1[]> result = _engineModule.engine_getClientVersionV1(callerVersion);

        await WriteSszResultAsync(ctx, result, SszCodec.EncodeClientVersionResponse);
    }
}
