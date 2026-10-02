// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/beacon/states/*</c>: fork, root, finality checkpoints, validators, committees, the pending queues and the proposer lookahead.
/// </summary>
internal static class BeaconStatesEndpoints
{
    private const int MaxValidatorIds = 64;

    /// <summary>One <c>postStateValidatorIdentities</c> SSZ entry: uint64 index, 48-byte pubkey, uint64 activation epoch.</summary>
    private const int ValidatorIdentitySszLength = sizeof(ulong) + BlsPublicKey.Length + sizeof(ulong);

    /// <summary>altair/validator.md <c>SYNC_COMMITTEE_SUBNET_COUNT</c>.</summary>
    private const int SyncCommitteeSubnetCount = 4;
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/beacon/states/{state_id}/fork", (HttpContext c, string state_id) => Fork(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/root", (HttpContext c, string state_id) => Root(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/finality_checkpoints", (HttpContext c, string state_id) => FinalityCheckpoints(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators", (HttpContext c, string state_id) => Validators(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators/{validator_id}", (HttpContext c, string state_id, string validator_id) => ValidatorById(c, state_id, validator_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validator_balances", (HttpContext c, string state_id) => ValidatorBalances(c, state_id, ctx.ForRequest()));
        app.MapPost("/eth/v1/beacon/states/{state_id}/validators", (HttpContext c, string state_id) => ValidatorsPost(c, state_id, ctx.ForRequest()));
        app.MapPost("/eth/v1/beacon/states/{state_id}/validator_balances", (HttpContext c, string state_id) => ValidatorBalancesPost(c, state_id, ctx.ForRequest()));
        app.MapPost("/eth/v1/beacon/states/{state_id}/validator_identities", (HttpContext c, string state_id) => ValidatorIdentities(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/committees", (HttpContext c, string state_id) => Committees(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/pending_deposits", (HttpContext c, string state_id) =>
            StateList(c, state_id, ctx.ForRequest(), static spec => spec.ElectraForkEpoch, static s => s.PendingDeposits!, static items => PendingDeposit.Encode(items), BeaconJsonWriter.WritePendingDepositsAsync));
        app.MapGet("/eth/v1/beacon/states/{state_id}/pending_partial_withdrawals", (HttpContext c, string state_id) =>
            StateList(c, state_id, ctx.ForRequest(), static spec => spec.ElectraForkEpoch, static s => s.PendingPartialWithdrawals!, static items => PendingPartialWithdrawal.Encode(items), BeaconJsonWriter.WritePendingPartialWithdrawalsAsync));
        app.MapGet("/eth/v1/beacon/states/{state_id}/pending_consolidations", (HttpContext c, string state_id) =>
            StateList(c, state_id, ctx.ForRequest(), static spec => spec.ElectraForkEpoch, static s => s.PendingConsolidations!, static items => PendingConsolidation.Encode(items), BeaconJsonWriter.WritePendingConsolidationsAsync));
        app.MapGet("/eth/v1/beacon/states/{state_id}/proposer_lookahead", (HttpContext c, string state_id) =>
            StateList(c, state_id, ctx.ForRequest(), static spec => spec.FuluForkEpoch, static s => s.ProposerLookahead!, static items => MemoryMarshal.AsBytes(items.AsSpan()).ToArray(), BeaconJsonWriter.WriteUIntArrayValueAsync));
        app.MapGet("/eth/v1/beacon/states/{state_id}/randao", (HttpContext c, string state_id) => Randao(c, state_id, ctx.ForRequest()));
        app.MapGet("/eth/v1/beacon/states/{state_id}/sync_committees", (HttpContext c, string state_id) => SyncCommittees(c, state_id, ctx.ForRequest()));
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getStateRandao</c>: the mix <c>get_randao_mix</c> returns for the requested epoch, by default the state's own.</summary>
    private static Task Randao(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!TryParseEpochQuery(c, out ulong? requestedEpoch))
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid epoch '{c.Request.Query["epoch"]}'.", c.RequestAborted);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        ulong stateEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong epoch = requestedEpoch ?? stateEpoch;
        // get_randao_mix's age check wraps for an epoch after the state's, which randao_mixes cannot hold yet.
        if (epoch > stateEpoch)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is after epoch {stateEpoch} of this state, so its randao_mixes hold no mix for it.", c.RequestAborted);
        }

        Hash256 mix;
        try
        {
            mix = state.GetRandaoMix(epoch);
        }
        catch (BeaconStateException e)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, e.Message, c.RequestAborted);
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, new RandaoDto(mix.ToString()),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    /// <summary>
    /// beacon-APIs v5.0.0-alpha.2 <c>getEpochSyncCommittees</c>: the registry indices of the sync committee
    /// for the requested epoch's period, in committee order, and their subnet slices.
    /// </summary>
    /// <remarks>
    /// A state holds only the committees of its own period and the next (altair/beacon-chain.md
    /// <c>current_sync_committee</c>, <c>next_sync_committee</c>); any other period is 400. Positions keep
    /// repeats, since get_next_sync_committee_indices may select a validator more than once.
    /// </remarks>
    private static Task SyncCommittees(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!TryParseEpochQuery(c, out ulong? requestedEpoch))
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid epoch '{c.Request.Query["epoch"]}'.", c.RequestAborted);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        ulong stateEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong epoch = requestedEpoch ?? stateEpoch;
        ulong statePeriod = stateEpoch / Presets.EpochsPerSyncCommitteePeriod;
        ulong period = epoch / Presets.EpochsPerSyncCommitteePeriod;
        SyncCommittee? committee = period == statePeriod ? state.CurrentSyncCommittee
            : period == statePeriod + 1 ? state.NextSyncCommittee
            : null;
        if (committee is null)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is outside the sync committee periods of this state: only period {statePeriod} (current) or {statePeriod + 1} (next) can be served.",
                c.RequestAborted);
        }

        // Indexes the 512 committee keys rather than the whole registry, then finds them in one registry scan.
        BlsPublicKey[] pubkeys = committee.Pubkeys!;
        Dictionary<BlsPublicKey, List<int>> positions = new(pubkeys.Length);
        for (int i = 0; i < pubkeys.Length; i++)
        {
            ref List<int>? at = ref CollectionsMarshal.GetValueRefOrAddDefault(positions, pubkeys[i], out _);
            (at ??= []).Add(i);
        }

        string[] validators = new string[pubkeys.Length];
        Validator[] registry = state.Validators!;
        for (int index = 0; index < registry.Length && positions.Count > 0; index++)
        {
            if (!positions.Remove(registry[index].Pubkey, out List<int>? at)) continue;
            string indexText = index.ToString();
            foreach (int position in at) validators[position] = indexText;
        }

        for (int i = 0; i < validators.Length; i++)
        {
            // A committee member is always a registry entry; an unset position means the state is corrupt.
            if (validators[i] is null)
            {
                return ApiErrors.Write(c, StatusCodes.Status500InternalServerError,
                    $"Sync committee member {pubkeys[i]} at position {i} is not in the validator registry of the state for '{stateId}' ({resolved.Root}).", c.RequestAborted);
            }
        }

        int subcommitteeSize = pubkeys.Length / SyncCommitteeSubnetCount;
        string[][] aggregates = new string[SyncCommitteeSubnetCount][];
        for (int subnet = 0; subnet < SyncCommitteeSubnetCount; subnet++)
        {
            aggregates[subnet] = validators[(subnet * subcommitteeSize)..((subnet + 1) * subcommitteeSize)];
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, new SyncCommitteeDto(validators, aggregates),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    /// <summary>Reads the optional <c>epoch</c> query parameter; <c>false</c> only when it is present and not a uint64.</summary>
    private static bool TryParseEpochQuery(HttpContext c, out ulong? epoch)
    {
        epoch = null;
        if (!c.Request.Query.TryGetValue("epoch", out StringValues raw)) return true;
        if (!ulong.TryParse(raw.ToString(), out ulong parsed)) return false;
        epoch = parsed;
        return true;
    }

    /// <summary>
    /// Serves one list field of the state as the beacon-APIs v5.0.0-alpha.2 <c>getPendingDeposits</c>,
    /// <c>getPendingPartialWithdrawals</c>, <c>getPendingConsolidations</c> and <c>getProposerLookahead</c>
    /// operations do: a versioned JSON envelope or the SSZ list, both with <c>Eth-Consensus-Version</c>.
    /// </summary>
    /// <remarks>
    /// Every list served here is a list of fixed-size elements, so its SSZ encoding is the elements' encodings concatenated.
    /// Each operation answers 400 for a state before the fork that introduced its field, so that check precedes decoding.
    /// </remarks>
    private static Task StateList<T>(HttpContext c, string stateId, BeaconApiContext ctx, Func<BeaconChainSpec, ulong> introducedAtEpoch,
        Func<BeaconStateFulu, T[]> select, Func<T[], byte[]> encodeSsz, Func<BeaconJsonStream, T[], Task> writeJson)
    {
        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!StateIdResolver.TryResolveRaw(ctx, stateId, out ResolvedRawState raw, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        ulong slot = BeaconStateCodec.ReadSlot(raw.Ssz);
        ulong forkEpoch = introducedAtEpoch(ctx.Spec);
        if (ctx.Spec.GetEpoch(slot) < forkEpoch)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                $"The state for '{stateId}' is at slot {slot}, before epoch {forkEpoch} where the requested field was introduced.", c.RequestAborted);
        }

        ResolvedState resolved = new(raw.Root, ApiStateDecoding.Decode(raw.Ssz, ctx.Spec));
        BeaconStateFulu state = resolved.State;
        T[] items = select(state);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, state.Slot);
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            return c.Response.Body.WriteAsync(encodeSsz(items), c.RequestAborted).AsTask();
        }

        return BeaconApiJson.WriteVersionedEnvelopeAsync(c, ResponseEnvelope.ForkName(ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(state.Slot))),
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            s => writeJson(s, items));
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
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.State, resolved.Root),
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
        if (!StateIdResolver.TryResolveBlock(ctx, stateId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        RootDto dto = new(resolved.StateRoot.ToString());
        return BeaconApiJson.WriteEnvelopeAsync(c, dto,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root),
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
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.State, resolved.Root),
            c.RequestAborted);
    }

    private static CheckpointDto ToCheckpointDto(Checkpoint checkpoint) =>
        new(checkpoint.Epoch.ToString(), checkpoint.Root!.ToString());

    private static Task Validators(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        List<string> idFilters = CollectQueryValues(c, "id", MaxValidatorIds + 1);
        if (idFilters.Count > MaxValidatorIds)
            return ApiErrors.Write(c, StatusCodes.Status414UriTooLong, "Too many validator IDs in request.", c.RequestAborted);
        return WriteValidators(c, stateId, ctx, idFilters, CollectQueryValues(c, "status"));
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>postStateValidators</c>: the GET filters carried in a JSON body, without the URI length limit.</summary>
    private static async Task ValidatorsPost(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        (bool read, ValidatorsRequestDto? request) = await TryReadJsonBody<ValidatorsRequestDto>(c, bodyRequired: true);
        if (!read) return;

        // The body schema makes statuses uniqueItems; repeats would multiply the per-validator status matching.
        string[] statuses = request?.Statuses ?? [];
        if (new HashSet<string>(statuses, StringComparer.Ordinal).Count != statuses.Length)
        {
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, "Validator statuses must be unique.", c.RequestAborted);
            return;
        }

        await WriteValidators(c, stateId, ctx, [.. request?.Ids ?? []], [.. statuses]);
    }

    private static Task WriteValidators(HttpContext c, string stateId, BeaconApiContext ctx, List<string> idFilters, List<string> statusFilters)
    {
        foreach (string filter in statusFilters)
        {
            if (!ValidatorStatus.IsValidFilter(filter))
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid validator status '{filter}'.", c.RequestAborted);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        Validator[] validators = state.Validators!;
        ulong[] balances = state.Balances!;
        ulong epoch = ctx.Spec.GetEpoch(state.Slot);

        HashSet<int>? indexFilter = null;
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        foreach (string id in idFilters)
        {
            ValidatorIdStatus lookup = TryResolveValidatorIndex(state, id, ref pubkeyIndex, out int index);
            if (lookup == ValidatorIdStatus.Invalid)
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                    $"Invalid validator id '{id}': expected an index or a 0x-prefixed 48-byte pubkey.", c.RequestAborted);
            }

            indexFilter ??= [];
            if (lookup == ValidatorIdStatus.Ok) indexFilter.Add(index);
            // A well-formed id that names no validator in this state contributes nothing (spec:
            // unrecognized ids are dropped from the response, not treated as an error).
        }

        List<ValidatorEntryDto> entries = [];
        for (int i = 0; i < validators.Length; i++)
        {
            if (indexFilter is not null && !indexFilter.Contains(i)) continue;

            string status = ValidatorStatus.Classify(validators[i], epoch);
            if (statusFilters.Count > 0 && !MatchesAny(status, statusFilters)) continue;

            entries.Add(ToValidatorEntry(i, validators[i], balances[i], status));
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    private static Task ValidatorById(HttpContext c, string stateId, string validatorId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        ValidatorIdStatus lookup = TryResolveValidatorIndex(state, validatorId, out int index);
        if (lookup == ValidatorIdStatus.Invalid)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                $"Invalid validator id '{validatorId}': expected an index or a 0x-prefixed 48-byte pubkey.", c.RequestAborted);
        }

        if (lookup == ValidatorIdStatus.NotFound)
        {
            return ApiErrors.Write(c, StatusCodes.Status404NotFound,
                $"Validator '{validatorId}' does not exist in this state.", c.RequestAborted);
        }

        ulong epoch = ctx.Spec.GetEpoch(state.Slot);
        Validator validator = state.Validators![index];
        ValidatorEntryDto entry = ToValidatorEntry(index, validator, state.Balances![index], ValidatorStatus.Classify(validator, epoch));

        return BeaconApiJson.WriteEnvelopeAsync(c, entry,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    private static Task ValidatorBalances(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        List<string> idFilters = CollectQueryValues(c, "id", MaxValidatorIds + 1);
        if (idFilters.Count > MaxValidatorIds)
            return ApiErrors.Write(c, StatusCodes.Status414UriTooLong, "Too many validator IDs in request.", c.RequestAborted);

        return WriteValidatorBalances(c, stateId, ctx, idFilters);
    }

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>postStateValidatorBalances</c>: the GET id filter carried in an optional JSON array body.</summary>
    private static async Task ValidatorBalancesPost(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        (bool read, string[]? ids) = await TryReadJsonBody<string[]>(c, bodyRequired: false);
        if (!read) return;

        await WriteValidatorBalances(c, stateId, ctx, [.. ids ?? []]);
    }

    private static Task WriteValidatorBalances(HttpContext c, string stateId, BeaconApiContext ctx, List<string> idFilters)
    {
        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        ulong[] balances = state.Balances!;
        if (!TryResolveIndices(state, idFilters, out List<int>? indices, out string? invalidId))
        {
            return WriteInvalidValidatorId(c, invalidId);
        }

        List<ValidatorBalanceEntryDto> entries = new(indices?.Count ?? balances.Length);
        if (indices is null)
        {
            for (int i = 0; i < balances.Length; i++)
            {
                entries.Add(new ValidatorBalanceEntryDto(i.ToString(), balances[i].ToString()));
            }
        }
        else
        {
            foreach (int index in indices)
            {
                entries.Add(new ValidatorBalanceEntryDto(index.ToString(), balances[index].ToString()));
            }
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    /// <summary>
    /// beacon-APIs v5.0.0-alpha.2 <c>postStateValidatorIdentities</c>: index, pubkey and activation epoch of the
    /// validators named by an optional JSON array body, or of every validator when it is absent or empty.
    /// </summary>
    /// <remarks>
    /// The operation names no SSZ container; the SSZ body is the list of the response's fixed-size
    /// <c>(index: uint64, pubkey: Bytes48, activation_epoch: uint64)</c> entries, in that field order.
    /// </remarks>
    private static async Task ValidatorIdentities(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        (bool read, string[]? ids) = await TryReadJsonBody<string[]>(c, bodyRequired: false);
        if (!read) return;

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            await ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
            return;
        }

        BeaconStateFulu state = resolved.State;
        Validator[] validators = state.Validators!;
        if (!TryResolveIndices(state, [.. ids ?? []], out List<int>? indices, out string? invalidId))
        {
            await WriteInvalidValidatorId(c, invalidId);
            return;
        }

        int count = indices?.Count ?? validators.Length;
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            byte[] ssz = new byte[count * ValidatorIdentitySszLength];
            for (int i = 0; i < count; i++)
            {
                int index = indices?[i] ?? i;
                Span<byte> entry = ssz.AsSpan(i * ValidatorIdentitySszLength, ValidatorIdentitySszLength);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)index);
                validators[index].Pubkey.Bytes.CopyTo(entry[sizeof(ulong)..]);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[(sizeof(ulong) + BlsPublicKey.Length)..], validators[index].ActivationEpoch);
            }

            c.Response.ContentType = ContentNegotiation.OctetStream;
            await c.Response.Body.WriteAsync(ssz, c.RequestAborted);
            return;
        }

        ValidatorIdentityDto[] entries = new ValidatorIdentityDto[count];
        for (int i = 0; i < count; i++)
        {
            int index = indices?[i] ?? i;
            entries[i] = new ValidatorIdentityDto(index.ToString(), validators[index].Pubkey.ToString(), validators[index].ActivationEpoch.ToString());
        }

        await BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    /// <summary>Resolves request ids to distinct registry indices in first-seen order, dropping well-formed ids that name no validator.</summary>
    /// <remarks>An index and a pubkey naming one validator count once, so repeated ids cannot multiply the response.</remarks>
    /// <param name="indices"><c>null</c> when <paramref name="ids"/> is empty, which selects every validator.</param>
    private static bool TryResolveIndices(BeaconStateFulu state, List<string> ids, out List<int>? indices, out string? invalidId)
    {
        indices = null;
        invalidId = null;
        if (ids.Count == 0) return true;

        indices = new List<int>(ids.Count);
        HashSet<int> seen = new(ids.Count);
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        foreach (string id in ids)
        {
            ValidatorIdStatus lookup = TryResolveValidatorIndex(state, id, ref pubkeyIndex, out int index);
            if (lookup == ValidatorIdStatus.Invalid)
            {
                invalidId = id;
                return false;
            }

            if (lookup == ValidatorIdStatus.Ok && seen.Add(index)) indices.Add(index);
        }

        return true;
    }

    private static Task WriteInvalidValidatorId(HttpContext c, string? id) =>
        ApiErrors.Write(c, StatusCodes.Status400BadRequest,
            $"Invalid validator id '{id}': expected an index or a 0x-prefixed 48-byte pubkey.", c.RequestAborted);

    /// <summary>Reads a POST JSON body, writing the error response itself when it cannot.</summary>
    /// <returns><c>Ok</c> is false once an error has been written; <c>Value</c> is <c>null</c> only for an absent optional body; a JSON <c>null</c> body is 400.</returns>
    private static async Task<(bool Ok, T? Value)> TryReadJsonBody<T>(HttpContext c, bool bodyRequired) where T : class
    {
        if (c.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
        {
            if (!bodyRequired) return (true, null);
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, "A JSON request body is required.", c.RequestAborted);
            return (false, null);
        }

        if (!ContentNegotiation.IsAcceptableContentType(c, ContentNegotiation.Json))
        {
            await ContentNegotiation.WriteUnsupportedMediaType(c, ContentNegotiation.Json);
            return (false, null);
        }

        try
        {
            T? value = await JsonSerializer.DeserializeAsync<T>(c.Request.Body, BeaconApiJson.Options, c.RequestAborted);
            if (value is not null) return (true, value);
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, "Malformed request body: a JSON null is not a filter.", c.RequestAborted);
        }
        catch (JsonException e)
        {
            await ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Malformed request body: {e.Message}", c.RequestAborted);
        }
        catch (BadHttpRequestException e)
        {
            await ApiErrors.Write(c, e.StatusCode, e.Message, c.RequestAborted);
        }

        return (false, null);
    }

    private static Task Committees(HttpContext c, string stateId, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        ulong currentEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong previousEpoch = currentEpoch == 0 ? 0 : currentEpoch - 1;
        ulong nextEpoch = currentEpoch + 1;

        ulong epoch = currentEpoch;
        if (c.Request.Query.TryGetValue("epoch", out StringValues epochRaw) && !ulong.TryParse(epochRaw.ToString(), out epoch))
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid epoch '{epochRaw}'.", c.RequestAborted);
        }

        // A single state's RandaoMixes vector only carries a real (non-stale) mix for the
        // previous/current/next epoch's seed lookahead window; outside that, GetSeed would silently
        // read a recycled entry and hand back a shuffling for the wrong epoch rather than failing -
        // exactly the "confidently wrong" failure mode this endpoint must not produce.
        if (epoch != previousEpoch && epoch != currentEpoch && epoch != nextEpoch)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is out of range for this state: only the previous ({previousEpoch}), current ({currentEpoch}) or next ({nextEpoch}) epoch's committees can be computed from a single state.",
                c.RequestAborted);
        }

        int? indexFilter = null;
        if (c.Request.Query.TryGetValue("index", out StringValues indexRaw))
        {
            if (!int.TryParse(indexRaw.ToString(), out int parsedIndex) || parsedIndex < 0)
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid committee index '{indexRaw}'.", c.RequestAborted);
            }

            indexFilter = parsedIndex;
        }

        ulong? slotFilter = null;
        if (c.Request.Query.TryGetValue("slot", out StringValues slotRaw))
        {
            if (!ulong.TryParse(slotRaw.ToString(), out ulong parsedSlot))
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Invalid slot '{slotRaw}'.", c.RequestAborted);
            }

            if (ctx.Spec.GetEpoch(parsedSlot) != epoch)
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Slot {parsedSlot} is not in epoch {epoch}.", c.RequestAborted);
            }

            slotFilter = parsedSlot;
        }

        CommitteeCache cache;
        try
        {
            cache = CommitteeCache.Build(state, epoch);
        }
        catch (BeaconStateException e)
        {
            return ApiErrors.Write(c, StatusCodes.Status400BadRequest, e.Message, c.RequestAborted);
        }

        ulong epochStartSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        List<CommitteeEntryDto> entries = [];
        for (ulong offset = 0; offset < Presets.SlotsPerEpoch; offset++)
        {
            ulong slot = epochStartSlot + offset;
            if (slotFilter is not null && slot != slotFilter) continue;

            for (int index = 0; index < cache.CommitteesPerSlot; index++)
            {
                if (indexFilter is not null && index != indexFilter) continue;

                ReadOnlySpan<int> members = cache.GetBeaconCommittee(slot, index);
                string[] memberIndices = new string[members.Length];
                for (int i = 0; i < members.Length; i++) memberIndices[i] = members[i].ToString();

                entries.Add(new CommitteeEntryDto(index.ToString(), slot.ToString(), memberIndices));
            }
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
            c.RequestAborted);
    }

    private enum ValidatorIdStatus { Ok, NotFound, Invalid }

    /// <summary>Resolves a beacon-api <c>validator_id</c> path/query segment: a decimal index or a 0x-prefixed pubkey.</summary>
    /// <remarks>Single-id call site only (<see cref="ValidatorById"/>): a pubkey id is resolved by an early-exit
    /// linear scan, which is cheaper than building a map for one lookup.</remarks>
    private static ValidatorIdStatus TryResolveValidatorIndex(BeaconStateFulu state, string id, out int index)
    {
        if (TryResolveNumericIndex(state.Validators!, id, out ValidatorIdStatus numericStatus, out index)) return numericStatus;

        if (HexConvert.TryParsePubKey(id, out BlsPublicKey pubkey))
        {
            Validator[] validators = state.Validators!;
            for (int i = 0; i < validators.Length; i++)
            {
                if (validators[i].Pubkey == pubkey)
                {
                    index = i;
                    return ValidatorIdStatus.Ok;
                }
            }

            return ValidatorIdStatus.NotFound;
        }

        return ValidatorIdStatus.Invalid;
    }

    /// <summary>
    /// Same contract as <see cref="TryResolveValidatorIndex(BeaconStateFulu, string, out int)"/>, but a pubkey id
    /// is resolved through <paramref name="pubkeyIndex"/> instead of a linear scan. The caller owns the map's
    /// lifetime - build it lazily on the first pubkey-form id in a request's id-filter loop and reuse it for every
    /// subsequent id, so an all-numeric-id (or id-less) request never pays for it. Building it turns what would
    /// otherwise be an O(validators) scan per pubkey id into one O(validators) build per request.
    /// </summary>
    private static ValidatorIdStatus TryResolveValidatorIndex(BeaconStateFulu state, string id, ref Dictionary<BlsPublicKey, int>? pubkeyIndex, out int index)
    {
        if (TryResolveNumericIndex(state.Validators!, id, out ValidatorIdStatus numericStatus, out index)) return numericStatus;

        if (HexConvert.TryParsePubKey(id, out BlsPublicKey pubkey))
        {
            pubkeyIndex ??= BuildPubkeyIndex(state.Validators!);
            return pubkeyIndex.TryGetValue(pubkey, out index) ? ValidatorIdStatus.Ok : ValidatorIdStatus.NotFound;
        }

        return ValidatorIdStatus.Invalid;
    }

    private static bool TryResolveNumericIndex(Validator[] validators, string id, out ValidatorIdStatus status, out int index)
    {
        index = -1;
        if (!ulong.TryParse(id, out ulong parsedIndex))
        {
            status = ValidatorIdStatus.Invalid;
            return false;
        }

        if (parsedIndex >= (ulong)validators.Length)
        {
            status = ValidatorIdStatus.NotFound;
            return true;
        }

        index = (int)parsedIndex;
        status = ValidatorIdStatus.Ok;
        return true;
    }

    /// <summary>Builds a one-off pubkey-to-index map for a single request's id-filter loop; not cached across requests.</summary>
    private static Dictionary<BlsPublicKey, int> BuildPubkeyIndex(Validator[] validators)
    {
        Dictionary<BlsPublicKey, int> map = new(validators.Length);
        for (int i = 0; i < validators.Length; i++)
        {
            map[validators[i].Pubkey] = i;
        }

        return map;
    }

    private static bool MatchesAny(string status, List<string> filters)
    {
        foreach (string filter in filters)
        {
            if (ValidatorStatus.MatchesFilter(status, filter)) return true;
        }

        return false;
    }

    /// <summary>Collects a repeatable query parameter, splitting comma-joined values the same way multiple <c>key=</c> occurrences would be.</summary>
    private static List<string> CollectQueryValues(HttpContext c, string key, int limit = int.MaxValue)
    {
        List<string> values = [];
        foreach (string? raw in c.Request.Query[key])
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (Range range in raw.AsSpan().Split(','))
            {
                ReadOnlySpan<char> part = raw.AsSpan()[range].Trim();
                if (part.IsEmpty) continue;
                values.Add(part.ToString());
                if (values.Count == limit) return values;
            }
        }

        return values;
    }

    private static ValidatorEntryDto ToValidatorEntry(int index, Validator validator, ulong balance, string status) => new(
        index.ToString(),
        balance.ToString(),
        status,
        new ValidatorContainerDto(
            validator.Pubkey.ToString(),
            validator.WithdrawalCredentials!.ToString(),
            validator.EffectiveBalance.ToString(),
            validator.Slashed,
            validator.ActivationEligibilityEpoch.ToString(),
            validator.ActivationEpoch.ToString(),
            validator.ExitEpoch.ToString(),
            validator.WithdrawableEpoch.ToString()));

    private sealed record ValidatorContainerDto(
        [property: JsonPropertyName("pubkey")] string Pubkey,
        [property: JsonPropertyName("withdrawal_credentials")] string WithdrawalCredentials,
        [property: JsonPropertyName("effective_balance")] string EffectiveBalance,
        [property: JsonPropertyName("slashed")] bool Slashed,
        [property: JsonPropertyName("activation_eligibility_epoch")] string ActivationEligibilityEpoch,
        [property: JsonPropertyName("activation_epoch")] string ActivationEpoch,
        [property: JsonPropertyName("exit_epoch")] string ExitEpoch,
        [property: JsonPropertyName("withdrawable_epoch")] string WithdrawableEpoch);

    private sealed record ValidatorEntryDto(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("balance")] string Balance,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("validator")] ValidatorContainerDto Validator);

    private sealed record ValidatorsRequestDto(
        [property: JsonPropertyName("ids")] string[]? Ids,
        [property: JsonPropertyName("statuses")] string[]? Statuses);

    private sealed record ValidatorIdentityDto(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("pubkey")] string Pubkey,
        [property: JsonPropertyName("activation_epoch")] string ActivationEpoch);

    private sealed record ValidatorBalanceEntryDto(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("balance")] string Balance);

    private sealed record CommitteeEntryDto(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("validators")] string[] Validators);

    private sealed record ForkDto(
        [property: JsonPropertyName("previous_version")] string PreviousVersion,
        [property: JsonPropertyName("current_version")] string CurrentVersion,
        [property: JsonPropertyName("epoch")] string Epoch);

    private sealed record RootDto([property: JsonPropertyName("root")] string Root);

    private sealed record RandaoDto([property: JsonPropertyName("randao")] string Randao);

    private sealed record SyncCommitteeDto(
        [property: JsonPropertyName("validators")] string[] Validators,
        [property: JsonPropertyName("validator_aggregates")] string[][] ValidatorAggregates);

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    private sealed record FinalityCheckpointsDto(
        [property: JsonPropertyName("previous_justified")] CheckpointDto PreviousJustified,
        [property: JsonPropertyName("current_justified")] CheckpointDto CurrentJustified,
        [property: JsonPropertyName("finalized")] CheckpointDto Finalized);
}
