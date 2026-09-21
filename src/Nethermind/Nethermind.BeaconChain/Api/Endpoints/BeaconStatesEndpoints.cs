// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/beacon/states/*</c>: fork, root, finality checkpoints, validators and committees.
/// </summary>
internal static class BeaconStatesEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/beacon/states/{state_id}/fork", (HttpContext c, string state_id) => Fork(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/root", (HttpContext c, string state_id) => Root(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/finality_checkpoints", (HttpContext c, string state_id) => FinalityCheckpoints(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators", (HttpContext c, string state_id) => Validators(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validators/{validator_id}", (HttpContext c, string state_id, string validator_id) => ValidatorById(c, state_id, validator_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/validator_balances", (HttpContext c, string state_id) => ValidatorBalances(c, state_id, ctx));
        app.MapGet("/eth/v1/beacon/states/{state_id}/committees", (HttpContext c, string state_id) => Committees(c, state_id, ctx));
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, resolved.State.Slot, resolved.Root),
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, resolved.Block.Message.Slot, resolved.Root),
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, resolved.State.Slot, resolved.Root),
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

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiErrors.Write(c, errorStatus, errorMessage!, c.RequestAborted);
        }

        BeaconStateFulu state = resolved.State;
        Validator[] validators = state.Validators!;
        ulong[] balances = state.Balances!;
        ulong epoch = ctx.Spec.GetEpoch(state.Slot);

        List<string> statusFilters = CollectQueryValues(c, "status");

        HashSet<int>? indexFilter = null;
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        foreach (string id in CollectQueryValues(c, "id"))
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, state.Slot, resolved.Root),
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, state.Slot, resolved.Root),
            c.RequestAborted);
    }

    private static Task ValidatorBalances(HttpContext c, string stateId, BeaconApiContext ctx)
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
        ulong[] balances = state.Balances!;
        List<string> idFilters = CollectQueryValues(c, "id");

        List<ValidatorBalanceEntryDto> entries = [];
        if (idFilters.Count == 0)
        {
            for (int i = 0; i < balances.Length; i++)
            {
                entries.Add(new ValidatorBalanceEntryDto(i.ToString(), balances[i].ToString()));
            }
        }
        else
        {
            Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
            foreach (string id in idFilters)
            {
                ValidatorIdStatus lookup = TryResolveValidatorIndex(state, id, ref pubkeyIndex, out int index);
                if (lookup == ValidatorIdStatus.Invalid)
                {
                    return ApiErrors.Write(c, StatusCodes.Status400BadRequest,
                        $"Invalid validator id '{id}': expected an index or a 0x-prefixed 48-byte pubkey.", c.RequestAborted);
                }

                if (lookup == ValidatorIdStatus.Ok)
                {
                    entries.Add(new ValidatorBalanceEntryDto(index.ToString(), balances[index].ToString()));
                }
            }
        }

        return BeaconApiJson.WriteEnvelopeAsync(c, entries,
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, state.Slot, resolved.Root),
            c.RequestAborted);
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            ResponseEnvelope.IsFinalized(ctx, state.Slot, resolved.Root),
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
    private static List<string> CollectQueryValues(HttpContext c, string key)
    {
        List<string> values = [];
        foreach (string? raw in c.Request.Query[key])
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (string part in raw.Split(','))
            {
                if (!string.IsNullOrWhiteSpace(part)) values.Add(part.Trim());
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

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    private sealed record FinalityCheckpointsDto(
        [property: JsonPropertyName("previous_justified")] CheckpointDto PreviousJustified,
        [property: JsonPropertyName("current_justified")] CheckpointDto CurrentJustified,
        [property: JsonPropertyName("finalized")] CheckpointDto Finalized);
}
