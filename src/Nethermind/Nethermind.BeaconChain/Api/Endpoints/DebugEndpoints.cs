// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/debug/*</c>: the persisted state as SSZ (the checkpoint-provider path) or JSON, and fork choice.
/// </summary>
internal sealed class DebugEndpoints(BeaconChainSpec spec, BeaconChainStore store, BeaconApiHead view)
{
    /// <summary>
    /// The beacon-api <c>getDebugForkChoice</c> shape (no <c>data</c> envelope): the checkpoints,
    /// one entry per proto-array node, and the proposer boost root under <c>extra_data</c>, all
    /// from the importer's latest published <see cref="ForkChoiceSnapshot"/>.
    /// </summary>
    [HttpGet("/eth/v1/debug/beacon/fork_choice"), HttpGet("/eth/v1/debug/fork_choice"), Negotiated]
    public IResult ForkChoice()
    {
        if (view.ForkChoiceSnapshot is not { } snapshot)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable,
                "Fork choice has not computed a head yet.");
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

        return ApiResults.Json(dto);
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
    [HttpGet("/eth/v2/debug/beacon/states/{stateId}"), Negotiated(Ssz = true)]
    public IResult State(string stateId) => ApiResults.SszOrJson(ssz: () => StateSsz(stateId), json: () => StateJson(stateId));

    private IResult StateSsz(string stateId)
    {
        if (!StateIdResolver.TryResolveRaw(store, view, stateId, out ResolvedRawState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ulong slot;
        if (view.GetCheckpointState(stateId) is { } checkpoint)
        {
            slot = checkpoint.State.Slot;
        }
        else if (!store.TryGetBlockSlot(resolved.Root, out slot))
        {
            // consensus-specs v1.7.0-beta.2, Electra/Fulu BeaconState: slot follows the 40-byte genesis fields.
            const int slotOffset = 40;
            if (resolved.Ssz.Length < slotOffset + sizeof(ulong))
            {
                return ApiResults.Error(StatusCodes.Status500InternalServerError,
                    $"The state persisted for '{stateId}' ({resolved.Root}) is not decodable: SSZ is too short to contain a slot.");
            }

            slot = BinaryPrimitives.ReadUInt64LittleEndian(resolved.Ssz.AsSpan(slotOffset));
        }

        return ApiResults.Ssz(resolved.Ssz).WithConsensusVersion(spec, slot);
    }

    private IResult StateJson(string stateId)
    {
        if (!StateIdResolver.TryResolveHeld(spec, store, view, stateId, out ResolvedState resolved))
        {
            if (!StateIdResolver.TryResolveRaw(store, view, stateId, out ResolvedRawState raw, out int errorStatus, out string? errorMessage))
            {
                return ApiResults.Error(errorStatus, errorMessage!);
            }

            try
            {
                // A fork before Electra throws UnsupportedForkException, which the host middleware turns into a labelled 501.
                resolved = new ResolvedState(raw.Root, ApiStateDecoding.Decode(raw.Ssz, spec));
            }
            catch (BeaconStateException e)
            {
                return ApiResults.Error(StatusCodes.Status500InternalServerError,
                    $"The state persisted for '{stateId}' ({raw.Root}) is not decodable: {e.Message}");
            }
        }

        ApiState state = resolved.State;
        return ApiResults.VersionedEnvelope(ResponseEnvelope.ForkName(state.Fork),
            ResponseEnvelope.ExecutionOptimistic(spec, store, view, resolved.Root),
            ResponseEnvelope.IsFinalized(spec, store, view, state, resolved.Root),
            s => state.Electra is { } electra
                ? BeaconJsonWriter.WriteBeaconStateAsync(s, electra)
                : BeaconJsonWriter.WriteBeaconStateAsync(s, state.Gloas!)).WithConsensusVersion(spec, state.Slot);
    }

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
