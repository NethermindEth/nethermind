// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
internal sealed class BeaconStatesEndpoints(BeaconApiContext ctx)
{
    private const int MaxValidatorIds = 64;

    /// <summary>One <c>postStateValidatorIdentities</c> SSZ entry: uint64 index, 48-byte pubkey, uint64 activation epoch.</summary>
    private const int ValidatorIdentitySszLength = sizeof(ulong) + BlsPublicKey.Length + sizeof(ulong);

    private const int IdentitySszEntriesPerWrite = 1024;

    /// <summary>altair/validator.md <c>SYNC_COMMITTEE_SUBNET_COUNT</c>.</summary>
    private const int SyncCommitteeSubnetCount = 4;

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>getStateRandao</c>: the mix <c>get_randao_mix</c> returns for the requested epoch, by default the state's own.</summary>
    [HttpGet("/eth/v1/beacon/states/{stateId}/randao"), Negotiated]
    public IResult Randao(string stateId, [FromQuery(Name = "epoch")] ulong? requestedEpoch)
    {
        if (!StateIdResolver.TryResolvePrefix(ctx, stateId, true, out ResolvedState resolved, out int errorStatus, out string? errorMessage, out byte[]? prefix))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        if (prefix is not null && (!ctx.Store.TryGetStateLength(resolved.Root, out int length) || length < StateIdResolver.CheckpointsEnd))
            return ApiResults.Error(StatusCodes.Status404NotFound, $"No state is retained for '{stateId}'.");

        ApiState state = resolved.State;
        ulong stateEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong epoch = requestedEpoch ?? stateEpoch;
        // get_randao_mix's age check wraps for an epoch after the state's, which randao_mixes cannot hold yet.
        if (epoch > stateEpoch)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is after epoch {stateEpoch} of this state, so its randao_mixes hold no mix for it.");
        }

        Hash256 mix;
        try
        {
            if (prefix is null)
            {
                mix = state.GetRandaoMix(epoch);
            }
            else
            {
                int position = BeaconStateAccessors.GetRandaoMixIndex(state.Slot, epoch);
                Span<byte> bytes = stackalloc byte[Hash256.Size];
                if (!ctx.Store.TryGetStateRange(resolved.Root, StateIdResolver.RandaoOffset + position * Hash256.Size, bytes))
                    return ApiResults.Error(StatusCodes.Status404NotFound, $"No state is retained for '{stateId}'.");
                mix = new Hash256(bytes);
            }
        }
        catch (BeaconStateException e)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, e.Message);
        }

        return StateEnvelope(resolved, new RandaoDto(mix.ToString()));
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
    [HttpGet("/eth/v1/beacon/states/{stateId}/sync_committees"), Negotiated]
    public IResult SyncCommittees(string stateId, [FromQuery(Name = "epoch")] ulong? requestedEpoch)
    {
        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        ulong stateEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong epoch = requestedEpoch ?? stateEpoch;
        ulong statePeriod = stateEpoch / Presets.EpochsPerSyncCommitteePeriod;
        ulong period = epoch / Presets.EpochsPerSyncCommitteePeriod;
        SyncCommittee? committee = period == statePeriod ? state.CurrentSyncCommittee
            : period == statePeriod + 1 ? state.NextSyncCommittee
            : null;
        if (committee is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is outside the sync committee periods of this state: only period {statePeriod} (current) or {statePeriod + 1} (next) can be served.");
        }

        BlsPublicKey[] pubkeys = committee.Pubkeys!;
        Dictionary<BlsPublicKey, int> indices = ctx.ValidatorIndices.Get(state.Validators);
        string[] validators = new string[pubkeys.Length];
        for (int i = 0; i < validators.Length; i++)
        {
            if (!indices.TryGetValue(pubkeys[i], out int index))
            {
                return ApiResults.Error(StatusCodes.Status500InternalServerError,
                    $"Sync committee member {pubkeys[i]} at position {i} is not in the validator registry of the state for '{stateId}' ({resolved.Root}).");
            }
            validators[i] = index.ToString();
        }

        int subcommitteeSize = pubkeys.Length / SyncCommitteeSubnetCount;
        string[][] aggregates = new string[SyncCommitteeSubnetCount][];
        for (int subnet = 0; subnet < SyncCommitteeSubnetCount; subnet++)
        {
            aggregates[subnet] = validators[(subnet * subcommitteeSize)..((subnet + 1) * subcommitteeSize)];
        }

        return StateEnvelope(resolved, new SyncCommitteeDto(validators, aggregates));
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/pending_deposits"), Negotiated(Ssz = true)]
    public IResult PendingDeposits(string stateId) =>
        StateList(stateId, static spec => spec.ElectraForkEpoch, static s => s.PendingDeposits!, static items => PendingDeposit.Encode(items), BeaconJsonWriter.WritePendingDepositsAsync);

    [HttpGet("/eth/v1/beacon/states/{stateId}/pending_partial_withdrawals"), Negotiated(Ssz = true)]
    public IResult PendingPartialWithdrawals(string stateId) =>
        StateList(stateId, static spec => spec.ElectraForkEpoch, static s => s.PendingPartialWithdrawals!, static items => PendingPartialWithdrawal.Encode(items), BeaconJsonWriter.WritePendingPartialWithdrawalsAsync);

    [HttpGet("/eth/v1/beacon/states/{stateId}/pending_consolidations"), Negotiated(Ssz = true)]
    public IResult PendingConsolidations(string stateId) =>
        StateList(stateId, static spec => spec.ElectraForkEpoch, static s => s.PendingConsolidations!, static items => PendingConsolidation.Encode(items), BeaconJsonWriter.WritePendingConsolidationsAsync);

    [HttpGet("/eth/v1/beacon/states/{stateId}/proposer_lookahead"), Negotiated(Ssz = true)]
    public IResult ProposerLookahead(string stateId) =>
        StateList(stateId, static spec => spec.FuluForkEpoch, static s => s.ProposerLookahead!, static items => MemoryMarshal.AsBytes(items.AsSpan()).ToArray(), BeaconJsonWriter.WriteUIntArrayValueAsync);

    /// <summary>
    /// Serves one list field of the state as the beacon-APIs v5.0.0-alpha.2 <c>getPendingDeposits</c>,
    /// <c>getPendingPartialWithdrawals</c>, <c>getPendingConsolidations</c> and <c>getProposerLookahead</c>
    /// operations do: a versioned JSON envelope or the SSZ list, both with <c>Eth-Consensus-Version</c>.
    /// </summary>
    /// <remarks>
    /// Every list served here is a list of fixed-size elements, so its SSZ encoding is the elements' encodings concatenated.
    /// Each operation answers 400 for a state before the fork that introduced its field, so that check precedes decoding.
    /// </remarks>
    private IResult StateList<T>(string stateId, Func<BeaconChainSpec, ulong> introducedAtEpoch,
        Func<ApiState, T[]> select, Func<T[], byte[]> encodeSsz, Func<BeaconJsonStream, T[], Task> writeJson)
    {
        if (!StateIdResolver.TryResolveHeld(ctx, stateId, out ResolvedState resolved))
        {
            // A persisted state is state-sized, so head lists never load one.
            if (stateId == "head")
            {
                return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "The head state is not available yet; retry later.");
            }

            if (!StateIdResolver.TryResolveRaw(ctx, stateId, out ResolvedRawState raw, out int errorStatus, out string? errorMessage))
            {
                return ApiResults.Error(errorStatus, errorMessage!);
            }

            ulong slot = BeaconStateCodec.ReadSlot(raw.Ssz);
            ulong forkEpoch = introducedAtEpoch(ctx.Spec);
            if (ctx.Spec.GetEpoch(slot) < forkEpoch)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest,
                    $"The state for '{stateId}' is at slot {slot}, before epoch {forkEpoch} where the requested field was introduced.");
            }

            resolved = new ResolvedState(raw.Root, ApiStateDecoding.Decode(raw.Ssz, ctx.Spec));
        }
        else if (ctx.Spec.GetEpoch(resolved.State.Slot) < introducedAtEpoch(ctx.Spec))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest,
                $"The state for '{stateId}' is at slot {resolved.State.Slot}, before epoch {introducedAtEpoch(ctx.Spec)} where the requested field was introduced.");
        }

        ApiState state = resolved.State;
        T[] items = select(state);
        return ApiResults.SszOrJson(
            ssz: () => ApiResults.Ssz(encodeSsz(items)),
            json: () => ApiResults.VersionedEnvelope(ResponseEnvelope.ForkName(ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(state.Slot))),
                ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
                ResponseEnvelope.IsFinalized(ctx, state, resolved.Root),
                s => writeJson(s, items))).WithConsensusVersion(ctx.Spec, state.Slot);
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/fork"), Negotiated]
    public IResult Fork(string stateId)
    {
        if (!StateIdResolver.TryResolvePrefix(ctx, stateId, true, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        Fork fork = resolved.State.ForkVersion;
        ForkDto dto = new(
            fork.PreviousVersion!.ToHexString(withZeroX: true),
            fork.CurrentVersion!.ToHexString(withZeroX: true),
            fork.Epoch.ToString());

        return StateEnvelope(resolved, dto);
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/root"), Negotiated]
    public IResult Root(string stateId)
    {
        if (ctx.GetCheckpointState(stateId) is { } checkpoint && StateIdResolver.TryResolveHeld(ctx, stateId, out ResolvedState held))
        {
            return StateEnvelope(held, new RootDto(checkpoint.StateRoot.ToString()));
        }
        // The state root is the block's own commitment to it; reading that field avoids decoding
        // the (often multi-hundred-MB) state just to re-hash it.
        if (!StateIdResolver.TryResolveBlock(ctx, stateId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        RootDto dto = new(resolved.StateRoot.ToString());
        return ApiResults.Envelope(dto,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.Slot, resolved.Root));
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/finality_checkpoints"), Negotiated]
    public IResult FinalityCheckpoints(string stateId)
    {
        if (!StateIdResolver.TryResolvePrefix(ctx, stateId, false, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        FinalityCheckpointsDto dto = new(
            ToCheckpointDto(resolved.State.PreviousJustifiedCheckpoint!),
            ToCheckpointDto(resolved.State.CurrentJustifiedCheckpoint!),
            ToCheckpointDto(resolved.State.FinalizedCheckpoint!));

        return StateEnvelope(resolved, dto);
    }

    private IResult StateEnvelope<T>(ResolvedState resolved, T data) =>
        ApiResults.Envelope(data,
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.State, resolved.Root));

    /// <summary>Writes <paramref name="entries"/> as the envelope's <c>data</c> array one element at a time, so a registry-sized response holds no registry-sized buffer while a slow reader drains it.</summary>
    private IResult StateEnvelopeStreamed<T>(ResolvedState resolved, IEnumerable<T> entries) =>
        ApiResults.StreamedEnvelope(
            ResponseEnvelope.ExecutionOptimistic(ctx, resolved.Root),
            ResponseEnvelope.IsFinalized(ctx, resolved.State, resolved.Root),
            async s =>
            {
                s.Writer.WriteStartArray();
                foreach (T entry in entries)
                {
                    JsonSerializer.Serialize(s.Writer, entry, BeaconApiJson.Options);
                    await s.CheckpointAsync();
                }

                s.Writer.WriteEndArray();
            });

    private static CheckpointDto ToCheckpointDto(Checkpoint checkpoint) =>
        new(checkpoint.Epoch.ToString(), checkpoint.Root!.ToString());

    [HttpGet("/eth/v1/beacon/states/{stateId}/validators"), Negotiated]
    public IResult Validators(string stateId, QueryValues id, QueryValues status) => id.Values.Count > MaxValidatorIds
        ? ApiResults.Error(StatusCodes.Status414UriTooLong, "Too many validator IDs in request.")
        : FilteredValidators(stateId, id.Values, status.Values);

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>postStateValidators</c>: the GET filters carried in a JSON body, without the URI length limit.</summary>
    [HttpPost("/eth/v1/beacon/states/{stateId}/validators"), Negotiated]
    public IResult ValidatorsPost(string stateId, [FromBody] ValidatorsRequestDto request) =>
        FilteredValidators(stateId, [.. request.Ids ?? []], [.. request.Statuses ?? []]);

    private IResult FilteredValidators(string stateId, List<string> idFilters, List<string> statusFilters)
    {
        if (statusFilters.Count > 13 || new HashSet<string>(statusFilters, StringComparer.Ordinal).Count != statusFilters.Count)
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Validator statuses must be unique.");

        foreach (string filter in statusFilters)
        {
            if (!ValidatorStatus.IsValidFilter(filter))
                return ApiResults.Error(StatusCodes.Status400BadRequest, $"Invalid validator status '{filter}'.");
        }

        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        Validator[] validators = state.Validators!;
        ulong[] balances = state.Balances!;
        if (balances.Length < validators.Length)
            return InconsistentRegistry(validators.Length, balances.Length);

        ulong epoch = ctx.Spec.GetEpoch(state.Slot);

        HashSet<int>? indexFilter = null;
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        foreach (string id in idFilters)
        {
            ValidatorIdStatus lookup = TryResolveValidatorIndex(ctx, state, id, ref pubkeyIndex, out int index);
            if (lookup == ValidatorIdStatus.Invalid)
            {
                return InvalidValidatorId(id);
            }

            indexFilter ??= [];
            if (lookup == ValidatorIdStatus.Ok) indexFilter.Add(index);
            // A well-formed id that names no validator in this state contributes nothing (spec:
            // unrecognized ids are dropped from the response, not treated as an error).
        }

        int[]? selectedIndices = indexFilter is null ? null : [.. indexFilter];
        selectedIndices?.AsSpan().Sort();

        IEnumerable<ValidatorEntryDto> SelectEntries()
        {
            int count = selectedIndices?.Length ?? validators.Length;
            for (int position = 0; position < count; position++)
            {
                int i = selectedIndices is null ? position : selectedIndices[position];

                string status = ValidatorStatus.Classify(validators[i], epoch);
                if (statusFilters.Count > 0 && !MatchesAny(status, statusFilters)) continue;

                yield return ToValidatorEntry(i, validators[i], balances[i], status);
            }
        }

        return StateEnvelopeStreamed(resolved, SelectEntries());
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/validators/{validatorId}"), Negotiated]
    public IResult ValidatorById(string stateId, string validatorId)
    {
        ResolvedState resolved = default;
        bool held = false;
        if (ulong.TryParse(validatorId, out ulong numericIndex)
            && StateIdResolver.TryResolvePrefix(ctx, stateId, false, out resolved, out _, out _, out byte[]? prefix))
        {
            held = prefix is null;
            if (prefix is not null && TryReadValidator(ctx, resolved, prefix, numericIndex, out Validator? item, out ulong balance))
            {
                if (item is null)
                    return ApiResults.Error(StatusCodes.Status404NotFound,
                        $"Validator '{validatorId}' does not exist in this state.");

                ulong stateEpoch = ctx.Spec.GetEpoch(resolved.State.Slot);
                return StateEnvelope(resolved,
                    ToValidatorEntry((int)numericIndex, item, balance, ValidatorStatus.Classify(item, stateEpoch)));
            }
        }

        if (!held && !StateIdResolver.TryResolve(ctx, stateId, out resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        ValidatorIdStatus lookup = ctx.GetHeldState(resolved.Root, stateId) is not null
            ? TryResolveValidatorIndex(ctx, state, validatorId, ref pubkeyIndex, out int index)
            : TryResolveValidatorIndex(state, validatorId, out index);
        if (lookup == ValidatorIdStatus.Invalid)
        {
            return InvalidValidatorId(validatorId);
        }

        if (lookup == ValidatorIdStatus.NotFound)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound,
                $"Validator '{validatorId}' does not exist in this state.");
        }

        ulong epoch = ctx.Spec.GetEpoch(state.Slot);
        Validator validator = state.Validators![index];
        ValidatorEntryDto entry = ToValidatorEntry(index, validator, state.Balances![index], ValidatorStatus.Classify(validator, epoch));

        return StateEnvelope(resolved, entry);
    }

    private static bool TryReadValidator(BeaconApiContext ctx, ResolvedState historical, ReadOnlySpan<byte> prefix, ulong index, out Validator? validator, out ulong balance)
    {
        validator = null;
        balance = 0;
        const int validatorSize = BlsPublicKey.Length + Hash256.Size + 5 * sizeof(ulong) + sizeof(byte);
        int historicalRoots = BinaryPrimitives.ReadInt32LittleEndian(prefix[StateIdResolver.HistoricalRootsOffset..]);
        int votes = BinaryPrimitives.ReadInt32LittleEndian(prefix[(StateIdResolver.RandaoOffset - 20)..]);
        int validators = BinaryPrimitives.ReadInt32LittleEndian(prefix[(StateIdResolver.RandaoOffset - 8)..]);
        int balances = BinaryPrimitives.ReadInt32LittleEndian(prefix[(StateIdResolver.RandaoOffset - 4)..]);
        int participation = BinaryPrimitives.ReadInt32LittleEndian(prefix[StateIdResolver.ParticipationOffset..]);
        int fixedLength = historical.State.Fork switch
        {
            BeaconFork.Electra => BeaconStateElectra.GetSszFixedSectionLength(),
            BeaconFork.Fulu => BeaconStateFulu.GetSszFixedSectionLength(),
            _ => BeaconStateGloas.GetSszFixedSectionLength(),
        };
        if (historicalRoots != fixedLength || votes < historicalRoots || validators < votes || balances < validators || participation < balances
            || (votes - historicalRoots) % Hash256.Size != 0 || (validators - votes) % 72 != 0
            || (balances - validators) % validatorSize != 0 || (participation - balances) % sizeof(ulong) != 0
            || !ctx.Store.TryGetStateLength(historical.Root, out int length) || participation > length)
            return false;

        if (index >= (ulong)((balances - validators) / validatorSize)) return true;
        if (index >= (ulong)((participation - balances) / sizeof(ulong))) return false;
        Span<byte> entry = stackalloc byte[validatorSize];
        Span<byte> balanceBytes = stackalloc byte[sizeof(ulong)];
        if (!ctx.Store.TryGetStateRange(historical.Root, validators + (int)index * validatorSize, entry)
            || !ctx.Store.TryGetStateRange(historical.Root, balances + (int)index * sizeof(ulong), balanceBytes))
            return false;
        Validator.Decode(entry, out validator);
        balance = BinaryPrimitives.ReadUInt64LittleEndian(balanceBytes);
        return true;
    }

    [HttpGet("/eth/v1/beacon/states/{stateId}/validator_balances"), Negotiated]
    public IResult ValidatorBalances(string stateId, QueryValues id) => id.Values.Count > MaxValidatorIds
        ? ApiResults.Error(StatusCodes.Status414UriTooLong, "Too many validator IDs in request.")
        : FilteredValidatorBalances(stateId, id.Values);

    /// <summary>beacon-APIs v5.0.0-alpha.2 <c>postStateValidatorBalances</c>: the GET id filter carried in an optional JSON array body.</summary>
    [HttpPost("/eth/v1/beacon/states/{stateId}/validator_balances"), Negotiated]
    public IResult ValidatorBalancesPost(string stateId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ImmutableArray<string> ids) =>
        FilteredValidatorBalances(stateId, ToIdFilters(ids));

    private IResult FilteredValidatorBalances(string stateId, List<string> idFilters)
    {
        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        ulong[] balances = state.Balances!;
        if (balances.Length < state.Validators!.Length)
            return InconsistentRegistry(state.Validators.Length, balances.Length);

        if (!TryResolveIndices(ctx, state, idFilters, out List<int>? indices, out string? invalidId))
        {
            return InvalidValidatorId(invalidId);
        }

        IEnumerable<ValidatorBalanceEntryDto> SelectEntries()
        {
            int count = indices?.Count ?? balances.Length;
            for (int i = 0; i < count; i++)
            {
                int index = indices?[i] ?? i;
                yield return new ValidatorBalanceEntryDto(index.ToString(), balances[index].ToString());
            }
        }

        return StateEnvelopeStreamed(resolved, SelectEntries());
    }

    /// <summary>
    /// beacon-APIs v5.0.0-alpha.2 <c>postStateValidatorIdentities</c>: index, pubkey and activation epoch of the
    /// validators named by an optional JSON array body, or of every validator when it is absent or empty.
    /// </summary>
    /// <remarks>
    /// The operation names no SSZ container; the SSZ body is the list of the response's fixed-size
    /// <c>(index: uint64, pubkey: Bytes48, activation_epoch: uint64)</c> entries, in that field order.
    /// </remarks>
    [HttpPost("/eth/v1/beacon/states/{stateId}/validator_identities"), Negotiated(Ssz = true)]
    public IResult ValidatorIdentities(string stateId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ImmutableArray<string> ids)
    {
        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        Validator[] validators = state.Validators!;
        if (!TryResolveIndices(ctx, state, ToIdFilters(ids), out List<int>? indices, out string? invalidId))
        {
            return InvalidValidatorId(invalidId);
        }

        int count = indices?.Count ?? validators.Length;
        return ApiResults.SszOrJson(
            ssz: () => ApiResults.Write(WriteSsz),
            json: () => StateEnvelopeStreamed(resolved, SelectEntries()));

        async Task WriteSsz(HttpContext c)
        {
            c.Response.ContentType = ContentNegotiation.OctetStream;
            byte[] ssz = new byte[Math.Min(count, IdentitySszEntriesPerWrite) * ValidatorIdentitySszLength];
            for (int start = 0; start < count; start += IdentitySszEntriesPerWrite)
            {
                int batch = Math.Min(count - start, IdentitySszEntriesPerWrite);
                for (int i = 0; i < batch; i++)
                {
                    int index = indices?[start + i] ?? start + i;
                    Span<byte> entry = ssz.AsSpan(i * ValidatorIdentitySszLength, ValidatorIdentitySszLength);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)index);
                    validators[index].Pubkey.Bytes.CopyTo(entry[sizeof(ulong)..]);
                    BinaryPrimitives.WriteUInt64LittleEndian(entry[(sizeof(ulong) + BlsPublicKey.Length)..], validators[index].ActivationEpoch);
                }

                await c.Response.Body.WriteAsync(ssz.AsMemory(0, batch * ValidatorIdentitySszLength), c.RequestAborted);
            }
        }

        IEnumerable<ValidatorIdentityDto> SelectEntries()
        {
            for (int i = 0; i < count; i++)
            {
                int index = indices?[i] ?? i;
                yield return new ValidatorIdentityDto(index.ToString(), validators[index].Pubkey.ToString(), validators[index].ActivationEpoch.ToString());
            }
        }
    }

    /// <summary>The ids of an optional JSON array body; an absent body binds to a default array and selects nothing.</summary>
    private static List<string> ToIdFilters(ImmutableArray<string> ids) => ids.IsDefault ? [] : [.. ids];

    /// <summary>Resolves request ids to distinct registry indices in first-seen order, dropping well-formed ids that name no validator.</summary>
    /// <remarks>An index and a pubkey naming one validator count once, so repeated ids cannot multiply the response.</remarks>
    /// <param name="indices"><c>null</c> when <paramref name="ids"/> is empty, which selects every validator.</param>
    private static bool TryResolveIndices(BeaconApiContext ctx, ApiState state, List<string> ids, out List<int>? indices, out string? invalidId)
    {
        indices = null;
        invalidId = null;
        if (ids.Count == 0) return true;

        indices = [with(ids.Count)];
        HashSet<int> seen = [with(ids.Count)];
        Dictionary<BlsPublicKey, int>? pubkeyIndex = null;
        foreach (string id in ids)
        {
            ValidatorIdStatus lookup = TryResolveValidatorIndex(ctx, state, id, ref pubkeyIndex, out int index);
            if (lookup == ValidatorIdStatus.Invalid)
            {
                invalidId = id;
                return false;
            }

            if (lookup == ValidatorIdStatus.Ok && seen.Add(index)) indices.Add(index);
        }

        return true;
    }

    /// <summary>Answers 500 before any byte of a streamed list is written, so a registry that cannot be listed never yields a truncated 200.</summary>
    private static IResult InconsistentRegistry(int validators, int balances) =>
        ApiResults.Error(StatusCodes.Status500InternalServerError,
            $"The state has {validators} validators but {balances} balances.");

    private static IResult InvalidValidatorId(string? id) =>
        ApiResults.Error(StatusCodes.Status400BadRequest,
            $"Invalid validator id '{id}': expected an index or a 0x-prefixed 48-byte pubkey.");

    [HttpGet("/eth/v1/beacon/states/{stateId}/committees"), Negotiated]
    public IResult Committees(string stateId, [FromQuery(Name = "epoch")] ulong? requestedEpoch, [FromQuery(Name = "index")] int? indexFilter, [FromQuery(Name = "slot")] ulong? slotFilter)
    {
        if (!StateIdResolver.TryResolve(ctx, stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage))
        {
            return ApiResults.Error(errorStatus, errorMessage!);
        }

        ApiState state = resolved.State;
        ulong currentEpoch = ctx.Spec.GetEpoch(state.Slot);
        ulong previousEpoch = currentEpoch == 0 ? 0 : currentEpoch - 1;
        ulong nextEpoch = currentEpoch + 1;

        ulong epoch = requestedEpoch ?? currentEpoch;

        // A single state's RandaoMixes vector only carries a real (non-stale) mix for the
        // previous/current/next epoch's seed lookahead window; outside that, GetSeed would silently
        // read a recycled entry and hand back a shuffling for the wrong epoch rather than failing -
        // exactly the "confidently wrong" failure mode this endpoint must not produce.
        if (epoch != previousEpoch && epoch != currentEpoch && epoch != nextEpoch)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest,
                $"Epoch {epoch} is out of range for this state: only the previous ({previousEpoch}), current ({currentEpoch}) or next ({nextEpoch}) epoch's committees can be computed from a single state.");
        }

        if (indexFilter < 0)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, $"Invalid committee index '{indexFilter}'.");
        }

        if (slotFilter is { } requestedSlot && ctx.Spec.GetEpoch(requestedSlot) != epoch)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, $"Slot {requestedSlot} is not in epoch {epoch}.");
        }

        CommitteeCache cache;
        try
        {
            cache = ctx.Committees.Get(state, epoch);
        }
        catch (BeaconStateException e)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, e.Message);
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

        return StateEnvelope(resolved, entries);
    }

    private enum ValidatorIdStatus { Ok, NotFound, Invalid }

    /// <summary>Resolves a beacon-api <c>validator_id</c> path/query segment: a decimal index or a 0x-prefixed pubkey.</summary>
    /// <remarks>For historical single-id requests, a pubkey id is resolved by an early-exit
    /// linear scan, which is cheaper than building a map for one lookup.</remarks>
    private static ValidatorIdStatus TryResolveValidatorIndex(ApiState state, string id, out int index)
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

    /// <summary>Resolves pubkeys through a registry index shared across requests; numeric IDs need no index.</summary>
    private static ValidatorIdStatus TryResolveValidatorIndex(BeaconApiContext ctx, ApiState state, string id, ref Dictionary<BlsPublicKey, int>? pubkeyIndex, out int index)
    {
        if (TryResolveNumericIndex(state.Validators!, id, out ValidatorIdStatus numericStatus, out index)) return numericStatus;

        if (HexConvert.TryParsePubKey(id, out BlsPublicKey pubkey))
        {
            pubkeyIndex ??= ctx.ValidatorIndices.Get(state.Validators!);
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

    private static bool MatchesAny(string status, List<string> filters)
    {
        foreach (string filter in filters)
        {
            if (ValidatorStatus.MatchesFilter(status, filter)) return true;
        }

        return false;
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

    internal sealed record ValidatorsRequestDto(
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
