// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Runner.JsonRpc;

/// <summary>
/// Rejects WebSocket requests whose <c>Origin</c> the JSON-RPC CORS policy does not allow, with HTTP 403.
/// </summary>
/// <remarks>
/// Browsers apply CORS to HTTP requests but not to WebSockets, so only the server can turn away a WebSocket that a web page
/// opens. This makes <see cref="IJsonRpcConfig.CorsOrigins"/> limit both. Requests without an <c>Origin</c> header come
/// from non-browser clients and are accepted, as in ASP.NET Core's <c>WebSocketOptions.AllowedOrigins</c> and in geth.
/// </remarks>
internal static class WebSocketOriginFilter
{
    public static void UseWebSocketOriginFilter(this IApplicationBuilder app, CorsPolicy policy, ILogger logger)
    {
        if (policy.AllowAnyOrigin) return;

        app.Use((ctx, next) =>
        {
            StringValues origin = ctx.Request.Headers.Origin;
            if (IsAllowed(origin, policy)) return next(ctx);

            // The client only sees 403, so the reason is in the node's log.
            if (logger.IsDebug) logger.Debug($"Refused a WebSocket connection from origin '{origin}', which JsonRpc.CorsOrigins does not allow.");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
    }

    internal static bool IsAllowed(StringValues origin, CorsPolicy policy) =>
        StringValues.IsNullOrEmpty(origin) || policy.AllowAnyOrigin || policy.IsOriginAllowed(origin.ToString());
}
