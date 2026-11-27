// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>The services behind the endpoints, and the middleware every request passes: content negotiation and the shared error/500 safety net.</summary>
internal static class BeaconApiEndpoints
{
    /// <summary>The whole of what an unauthenticated caller learns about an undecodable state: a fixed
    /// sentence, not whatever the codec put in its exception.</summary>
    internal const string UnsupportedForkMessage = "This node cannot decode the requested beacon state: its fork predates Electra, the first fork this driver decodes";

    /// <summary>Registers endpoint dependencies and the head view captured for each request.</summary>
    public static void AddServices(IServiceCollection services,
        BeaconChainSpec spec, IBeaconChainStatusSource statusSource, SlotClock slotClock,
        BeaconChainStore store, LocalMetadataSource metadataSource, IEngineDriver engine,
        ILogManager logManager, CommitteeCacheLru committees, BeaconP2P? p2p = null, PeerManager? peerManager = null,
        BeaconDiscovery? discovery = null, ForkChoiceSnapshotHolder? forkChoiceSnapshots = null,
        HeadSnapshotHolder? headSnapshots = null, DataColumnSidecarPool? columnPool = null)
    {
        Func<BeaconApiHead> captureHead = () => BeaconApiHead.Capture(statusSource, headSnapshots, forkChoiceSnapshots);
        services.ConfigureHttpJsonOptions(static options => options.SerializerOptions.PropertyNameCaseInsensitive = false);
        services.AddSingleton(spec);
        services.AddSingleton(slotClock);
        services.AddSingleton(store);
        services.AddSingleton(metadataSource);
        services.AddSingleton(engine);
        services.AddSingleton(logManager);
        if (p2p is not null) services.AddSingleton(p2p);
        if (peerManager is not null) services.AddSingleton(peerManager);
        if (discovery is not null) services.AddSingleton(discovery);
        if (columnPool is not null) services.AddSingleton(columnPool);
        services.AddSingleton(captureHead);
        services.AddScoped(_ => captureHead());
        services.AddSingleton<ValidatorIndexCache>();
        services.AddSingleton(committees);
        services.AddSingleton<StateCommitteeCache>();
        services.AddSingleton(_ => new System.Threading.RateLimiting.ConcurrencyLimiter(new()
        {
            PermitLimit = BlobsEndpoint.MaxConcurrentRebuilds,
            QueueLimit = 0,
        }));
        services.AddSingleton<EventsEndpoint>();
    }

    public static void UseMiddleware(WebApplication app, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger(typeof(BeaconApiEndpoints));

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
            catch (BadHttpRequestException e) when (!httpCtx.RequestAborted.IsCancellationRequested)
            {
                // The binder's own message names C# types.
                await ApiErrors.Write(httpCtx, e.StatusCode, e.InnerException is JsonException malformed
                    ? $"Malformed request body: {malformed.Message}"
                    : "A request parameter or the request body is missing or malformed.");
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

        app.Use(NegotiateAsync);
    }

    /// <summary>Checks the media types of a request to a <see cref="NegotiatedAttribute"/> endpoint before its parameters are bound.</summary>
    /// <remarks>An endpoint filter would run after binding, so a malformed parameter or body would hide an unacceptable <c>Accept</c> header.</remarks>
    private static async Task NegotiateAsync(HttpContext c, RequestDelegate next)
    {
        if (c.GetEndpoint()?.Metadata is not { } metadata || metadata.GetMetadata<NegotiatedAttribute>() is not { } negotiated)
        {
            await next(c);
            return;
        }

        // Captured before the request is judged, so a node that cannot produce a view answers 500 whatever was asked.
        c.RequestServices.GetRequiredService<BeaconApiHead>();
        if (ContentNegotiation.Negotiate(c, negotiated.Ssz) is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        if (metadata.GetMetadata<IAcceptsMetadata>() is not null && c.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            if (!ContentNegotiation.IsJsonContentType(c))
            {
                await ContentNegotiation.WriteUnsupportedMediaType(c);
                return;
            }

            // RFC 8259 section 11 defines no charset parameter, and the binder throws on one it does not know.
            c.Request.ContentType = ContentNegotiation.Json;
        }

        await next(c);

        // The binder answers a body Kestrel refuses, such as one that is too large, with a bare status.
        if (c.Response is { StatusCode: >= StatusCodes.Status400BadRequest, HasStarted: false, ContentType: null })
        {
            await ApiErrors.Write(c, c.Response.StatusCode, ReasonPhrases.GetReasonPhrase(c.Response.StatusCode), c.RequestAborted);
        }
    }
}
