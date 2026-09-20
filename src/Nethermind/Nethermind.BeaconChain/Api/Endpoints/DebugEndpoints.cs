// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/debug/*</c>: the persisted state as SSZ (the checkpoint-provider path) or JSON, and fork choice.
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
    /// Serves the node's persisted post-state: as the raw stored SSZ bytes (what lets another client
    /// checkpoint-sync from this node) or, decoded, as the beacon-api JSON representation.
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
            return StateJson(c, stateId, resolved, ctx);
        }

        // The raw path never decodes the state; the block it belongs to is the cheap source of its
        // slot for the version header. Without that block the header is omitted, never guessed.
        if (ctx.Store.TryGetBlock(resolved.Root, out SignedBeaconBlock? block))
        {
            ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, block.Message!.Slot);
        }

        c.Response.ContentType = ContentNegotiation.OctetStream;
        return c.Response.Body.WriteAsync(resolved.Ssz, c.RequestAborted).AsTask();
    }

    private static Task StateJson(HttpContext c, string stateId, ResolvedRawState resolved, BeaconApiContext ctx)
    {
        BeaconStateFulu state;
        try
        {
            // Throws NotSupportedException for a fork this driver cannot decode (Gloas); the host
            // middleware turns that into a labelled 501 rather than a fabricated Fulu-shaped body.
            state = BeaconStateCodec.Decode(resolved.Ssz, ctx.Spec);
        }
        catch (BeaconStateException e)
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"The state persisted for '{stateId}' ({resolved.Root}) is not decodable: {e.Message}", c.RequestAborted);
        }

        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(state.Slot));
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, state.Slot);
        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, state.Slot),
            s => BeaconJsonWriter.WriteBeaconStateAsync(s, state));
    }
}
