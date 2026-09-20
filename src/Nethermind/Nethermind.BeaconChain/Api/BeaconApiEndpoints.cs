// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>Wires every endpoint group onto the host, plus the shared error/500 safety net.</summary>
internal static class BeaconApiEndpoints
{
    public static void MapAll(WebApplication app, BeaconApiContext ctx)
    {
        ILogger logger = ctx.LogManager.GetClassLogger(typeof(BeaconApiEndpoints));

        app.Use(async (httpCtx, next) =>
        {
            try
            {
                await next(httpCtx);
            }
            catch (Exception e) when (!httpCtx.RequestAborted.IsCancellationRequested)
            {
                if (logger.IsError) logger.Error($"Beacon API handler failed for {httpCtx.Request.Method} {httpCtx.Request.Path}", e);
                await ApiErrors.Write(httpCtx, StatusCodes.Status500InternalServerError, "Internal server error");
            }
        });

        NodeEndpoints.Map(app, ctx);
        ConfigEndpoints.Map(app, ctx);
        BeaconEndpoints.Map(app, ctx);
        BeaconStatesEndpoints.Map(app, ctx);
        DebugEndpoints.Map(app, ctx);
        ValidatorEndpoints.Map(app, ctx);
        EventsEndpoint.Map(app, ctx);

        app.MapFallback(c => ApiErrors.Write(c, StatusCodes.Status404NotFound, "Unknown endpoint", c.RequestAborted));
    }
}
