// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/debug/*</c>: raw SSZ state (the checkpoint-provider path) and fork choice.
/// </summary>
internal static class DebugEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v2/debug/beacon/states/{state_id}", (HttpContext c, string state_id) => State(c, state_id, ctx));

        // ForkChoiceRunner is instantiated privately inside BlockImporter (one per running sync
        // session via BlockImporterFactory) and is not registered in the container, so this host has
        // no resolvable reference to fork-choice internals to dump.
        app.MapGet("/eth/v1/debug/beacon/fork_choice", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
            "Fork choice internals are not exposed as a resolvable service (ForkChoiceRunner is owned privately by the block importer).", c.RequestAborted));
    }

    /// <summary>
    /// Serves the node's persisted post-state as raw SSZ — this is what lets another client
    /// checkpoint-sync from this node.
    /// </summary>
    private static Task State(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (!StateIdResolver.TryResolveRaw(ctx, stateId, out ResolvedRawState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (format == ContentNegotiation.ResponseFormat.Json)
        {
            // A full multi-fork BeaconState is hundreds of SSZ fields (validator registry,
            // balances, history vectors, PeerDAS/ePBS additions); hand-writing a correct JSON
            // mapping for all of them was out of scope for this milestone.
            return ApiErrors.Write(c, StatusCodes.Status501NotImplemented,
                "JSON state bodies are not implemented; request 'Accept: application/octet-stream' for SSZ.", c.RequestAborted);
        }

        c.Response.ContentType = ContentNegotiation.OctetStream;
        return c.Response.Body.WriteAsync(resolved.Ssz, c.RequestAborted).AsTask();
    }
}
