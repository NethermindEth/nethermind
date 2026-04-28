// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/client/version</c>, the SSZ-REST equivalent of
/// <c>engine_getClientVersionV1</c>.
/// </summary>
public sealed class ClientVersionSszHandler : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "client/version";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        _ = SszCodec.DecodeClientVersionRequest(body.Span);

        await WriteSszPooledAsync(ctx, SszCodec.EncodeClientVersionResponse([new ClientVersionV1()]));
    }
}
