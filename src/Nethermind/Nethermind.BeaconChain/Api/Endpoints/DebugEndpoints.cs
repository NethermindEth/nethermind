// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/debug/*</c>: the persisted state as SSZ (the checkpoint-provider path) or JSON, and fork choice.
/// </summary>
internal static class DebugEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v2/debug/beacon/states/{state_id}", (HttpContext c, string state_id) => State(c, state_id, ctx));
        app.MapGet("/eth/v1/debug/beacon/fork_choice", c => ForkChoice(c, ctx));
    }

    /// <summary>
    /// The beacon-api <c>getDebugForkChoice</c> shape (no <c>data</c> envelope): the checkpoints,
    /// one entry per proto-array node, and the proposer boost root under <c>extra_data</c>, all
    /// from the importer's latest published <see cref="ForkChoiceSnapshot"/>.
    /// </summary>
    private static Task ForkChoice(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (ctx.ForkChoiceSnapshots?.Current is not { } snapshot)
        {
            return ApiErrors.Write(c, StatusCodes.Status503ServiceUnavailable,
                "Fork choice has not computed a head yet.", c.RequestAborted);
        }

        ForkChoiceNodeDto[] nodes = new ForkChoiceNodeDto[snapshot.Nodes.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            ForkChoiceSnapshotNode node = snapshot.Nodes[i];
            nodes[i] = new ForkChoiceNodeDto(
                node.Slot.ToString(),
                node.Root.ToString(),
                node.ParentRoot?.ToString() ?? Hash256.Zero.ToString(),
                node.JustifiedEpoch.ToString(),
                node.FinalizedEpoch.ToString(),
                node.Weight.ToString(),
                ValidityWireName(node.ExecutionStatus),
                node.ExecutionBlockHash?.ToString() ?? Hash256.Zero.ToString());
        }

        ForkChoiceDto dto = new(
            ToCheckpointDto(snapshot.JustifiedCheckpoint),
            ToCheckpointDto(snapshot.FinalizedCheckpoint),
            nodes,
            new ForkChoiceExtraDataDto(snapshot.ProposerBoostRoot.ToString()));

        c.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(c.Response.Body, dto, BeaconApiJson.Options, c.RequestAborted);
    }

    private static CheckpointDto ToCheckpointDto(CheckpointRef checkpoint) => new(checkpoint.Epoch.ToString(), checkpoint.Root.ToString());

    /// <summary>The spec's <c>validity</c> enum has no pre-merge value; a payload-less block has nothing to invalidate, and this driver never holds one (Electra onwards only).</summary>
    private static string ValidityWireName(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Valid or ExecutionStatus.Irrelevant => "valid",
        ExecutionStatus.Invalid => "invalid",
        ExecutionStatus.Optimistic => "optimistic",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

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
            state = ApiStateDecoding.Decode(resolved.Ssz, ctx.Spec);
        }
        catch (BeaconStateException e)
        {
            return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                $"The state persisted for '{stateId}' ({resolved.Root}) is not decodable: {e.Message}", c.RequestAborted);
        }

        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(state.Slot));
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, state.Slot);
        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(fork),
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            s => BeaconJsonWriter.WriteBeaconStateAsync(s, state));
    }

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    // parent_root and execution_block_hash are required by the beacon-API Node schema, so a node
    // that has neither carries the zero root rather than omitting the field.
    private sealed record ForkChoiceNodeDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("block_root")] string BlockRoot,
        [property: JsonPropertyName("parent_root")] string ParentRoot,
        [property: JsonPropertyName("justified_epoch")] string JustifiedEpoch,
        [property: JsonPropertyName("finalized_epoch")] string FinalizedEpoch,
        [property: JsonPropertyName("weight")] string Weight,
        [property: JsonPropertyName("validity")] string Validity,
        [property: JsonPropertyName("execution_block_hash")] string ExecutionBlockHash);

    private sealed record ForkChoiceExtraDataDto([property: JsonPropertyName("proposer_boost_root")] string ProposerBoostRoot);

    private sealed record ForkChoiceDto(
        [property: JsonPropertyName("justified_checkpoint")] CheckpointDto JustifiedCheckpoint,
        [property: JsonPropertyName("finalized_checkpoint")] CheckpointDto FinalizedCheckpoint,
        [property: JsonPropertyName("fork_choice_nodes")] ForkChoiceNodeDto[] ForkChoiceNodes,
        [property: JsonPropertyName("extra_data")] ForkChoiceExtraDataDto ExtraData);
}
