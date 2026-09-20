// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/beacon/states/*</c>: fork, root, finality checkpoints. Validators and committees are
/// deliberately not implemented here (see the report) rather than approximated.
/// </summary>
internal static class BeaconStatesEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/beacon/states/{state_id}/fork", (HttpContext c, string state_id) => Fork(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/root", (HttpContext c, string state_id) => Root(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/finality_checkpoints", (HttpContext c, string state_id) => FinalityCheckpoints(c, state_id, ctx));

        // Validator listing needs a status classification (pending/active/exited/...) computed from
        // the full validator set against the current epoch, and committees need the shuffling
        // function; both are substantial state-transition-adjacent logic this milestone did not
        // implement, and a partial/simplified version risks being confidently wrong rather than
        // absent. See notDone in the report.
        const string message = "Validator and committee listings are not implemented in this milestone.";
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, message, c.RequestAborted));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators/{validator_id}", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, message, c.RequestAborted));
        app.MapGet("/eth/v1/beacon/states/{state_id}/committees", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, message, c.RequestAborted));
    }

    private static Task Fork(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        Fork fork = resolved.State.Fork!;
        ForkDto dto = new(
            fork.PreviousVersion!.ToHexString(withZeroX: true),
            fork.CurrentVersion!.ToHexString(withZeroX: true),
            fork.Epoch.ToString());

        return BeaconApiJson.WriteEnvelopeAsync(c, dto,
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.State.Slot),
            c.RequestAborted);
    }

    private static Task Root(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        // The state root is the block's own commitment to it; reading that field avoids decoding
        // the (often multi-hundred-MB) state just to re-hash it.
        if (!BlockIdResolver.TryResolve(ctx, stateId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        RootDto dto = new(resolved.Block.Message!.StateRoot!.ToString());
        return BeaconApiJson.WriteEnvelopeAsync(c, dto,
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.Block.Message.Slot),
            c.RequestAborted);
    }

    private static Task FinalityCheckpoints(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        FinalityCheckpointsDto dto = new(
            ToCheckpointDto(resolved.State.PreviousJustifiedCheckpoint!),
            ToCheckpointDto(resolved.State.CurrentJustifiedCheckpoint!),
            ToCheckpointDto(resolved.State.FinalizedCheckpoint!));

        return BeaconApiJson.WriteEnvelopeAsync(c, dto,
            ResponseEnvelope.ExecutionOptimistic(),
            ResponseEnvelope.IsFinalized(ctx.Spec, ctx.StatusSource, resolved.State.Slot),
            c.RequestAborted);
    }

    private static CheckpointDto ToCheckpointDto(Checkpoint checkpoint) =>
        new(checkpoint.Epoch.ToString(), checkpoint.Root!.ToString());

    private sealed record ForkDto(
        [property: JsonPropertyName("previous_version")] string PreviousVersion,
        [property: JsonPropertyName("current_version")] string CurrentVersion,
        [property: JsonPropertyName("epoch")] string Epoch);

    private sealed record RootDto([property: JsonPropertyName("root")] string Root);

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    private sealed record FinalityCheckpointsDto(
        [property: JsonPropertyName("previous_justified")] CheckpointDto PreviousJustified,
        [property: JsonPropertyName("current_justified")] CheckpointDto CurrentJustified,
        [property: JsonPropertyName("finalized")] CheckpointDto Finalized);
}
