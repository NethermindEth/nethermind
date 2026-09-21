// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>Wires every endpoint group onto the host, plus the shared error/500 safety net.</summary>
internal static class BeaconApiEndpoints
{
    /// <summary>The whole of what an unauthenticated caller learns about an undecodable state: a fixed
    /// sentence, not whatever the codec put in its exception.</summary>
    internal const string UnsupportedForkMessage = "This node cannot decode the requested beacon state: its fork is not supported by this driver, which processes Fulu states only";

    public static void MapAll(WebApplication app, BeaconApiContext ctx)
    {
        ILogger logger = ctx.LogManager.GetClassLogger(typeof(BeaconApiEndpoints));

        app.Use(async (httpCtx, next) =>
        {
            try
            {
                await next(httpCtx);
            }
            catch (UnsupportedForkException e) when (!httpCtx.RequestAborted.IsCancellationRequested)
            {
                // Only the API's own boundary type (ApiStateDecoding) gets the 501: the node lacks a
                // capability, it is not malfunctioning. Catching NotSupportedException itself here
                // turned every unrelated one from any layer into a confident "not implemented" that
                // also echoed its internal message to the caller.
                if (logger.IsWarn) logger.Warn($"Beacon API request for {httpCtx.Request.Method} {httpCtx.Request.Path} named a fork this driver cannot process: {e.InnerException?.Message ?? e.Message}");
                await ApiErrors.Write(httpCtx, StatusCodes.Status501NotImplemented, UnsupportedForkMessage);
            }
            catch (Exception e) when (!httpCtx.RequestAborted.IsCancellationRequested)
            {
                if (logger.IsError)
                {
                    string outcome = httpCtx.Response.HasStarted ? "; the response had already started, so the connection is aborted" : "";
                    logger.Error($"Beacon API handler failed for {httpCtx.Request.Method} {httpCtx.Request.Path}{outcome}", e);
                }
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
