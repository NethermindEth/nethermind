// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Api;

/// <summary>The services behind the endpoints, and the middleware every request passes: content negotiation and the shared error/500 safety net.</summary>
internal static class BeaconApiEndpoints
{
    /// <summary>The whole of what an unauthenticated caller learns about an undecodable state: a fixed
    /// sentence, not whatever the codec put in its exception.</summary>
    internal const string UnsupportedForkMessage = "This node cannot decode the requested beacon state: its fork predates Electra, the first fork this driver decodes";

    /// <summary>Registers the view of <paramref name="ctx"/> each request reads, and configures parameter binding.</summary>
    public static void AddServices(IServiceCollection services, BeaconApiContext ctx) => services
        .ConfigureHttpJsonOptions(static options => options.SerializerOptions.PropertyNameCaseInsensitive = false)
        .AddScoped(_ => ctx.ForRequest())
        .AddSingleton(services => new EventsEndpoint(ctx, services.GetRequiredService<IHostApplicationLifetime>()));

    public static void UseMiddleware(WebApplication app, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger(typeof(BeaconApiEndpoints));

        app.Use(async (httpCtx, next) =>
        {
            try
            {
                await next(httpCtx);
            }
            catch (BeaconNodeApiException e) when (!httpCtx.RequestAborted.IsCancellationRequested)
            {
                // A refusal of the validator API is part of its contract, so its message is written for the caller.
                await ApiErrors.Write(httpCtx, e.Error switch
                {
                    BeaconNodeApiError.InvalidRequest => StatusCodes.Status400BadRequest,
                    BeaconNodeApiError.NotFound => StatusCodes.Status404NotFound,
                    BeaconNodeApiError.NotSupported => StatusCodes.Status501NotImplemented,
                    BeaconNodeApiError.NotReady => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status500InternalServerError,
                }, e.Message);
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
        c.RequestServices.GetRequiredService<BeaconApiContext>();
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
