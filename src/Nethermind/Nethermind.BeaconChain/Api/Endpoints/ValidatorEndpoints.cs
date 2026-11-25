// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// The validator-duty routes, each a thin handler over <see cref="IBeaconNodeValidatorApi"/>: it decodes the request, makes one call
/// and encodes the result. <see cref="BeaconApiEndpoints"/> turns a <see cref="BeaconNodeApiException"/> into its status, and every
/// other <c>/eth/v1/validator/*</c> route answers through it as not supported.
/// </summary>
internal sealed class ValidatorEndpoints(BeaconApiContext ctx)
{
    [HttpPost("/eth/v1/validator/duties/attester/{epoch}"), Negotiated]
    public async Task<IResult> AttesterDuties(string epoch, [FromBody] string[] ids, CancellationToken cancellationToken)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch)) return Invalid($"Invalid epoch '{epoch}'.");
        if (!TryParseValidatorIndices(ids, out ulong[]? indices, out string? invalidId)) return Invalid($"Invalid validator index '{invalidId}'.");
        DutiesResult<AttesterDuty> duties = await ctx.ValidatorApi.GetAttesterDuties(parsedEpoch, indices, cancellationToken);
        return Duties(duties, static duty => new AttesterDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), duty.CommitteeIndex.ToString(),
            duty.CommitteeLength.ToString(), duty.CommitteesAtSlot.ToString(), duty.ValidatorCommitteeIndex.ToString(), duty.Slot.ToString()));
    }

    [HttpGet("/eth/v2/validator/duties/proposer/{epoch}"), Negotiated]
    public async Task<IResult> ProposerDuties(string epoch, CancellationToken cancellationToken)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch)) return Invalid($"Invalid epoch '{epoch}'.");
        DutiesResult<ProposerDuty> duties = await ctx.ValidatorApi.GetProposerDuties(parsedEpoch, cancellationToken);
        return Duties(duties, static duty => new ProposerDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), duty.Slot.ToString()));
    }

    [HttpPost("/eth/v1/validator/duties/sync/{epoch}"), Negotiated]
    public async Task<IResult> SyncDuties(string epoch, [FromBody] string[] ids, CancellationToken cancellationToken)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch)) return Invalid($"Invalid epoch '{epoch}'.");
        if (!TryParseValidatorIndices(ids, out ulong[]? indices, out string? invalidId)) return Invalid($"Invalid validator index '{invalidId}'.");
        DutiesResult<SyncDuty> duties = await ctx.ValidatorApi.GetSyncDuties(parsedEpoch, indices, cancellationToken);
        return Duties(duties, static duty => new SyncDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), ToStrings(duty.ValidatorSyncCommitteeIndices)));
    }

    [HttpGet("/eth/v1/validator/attestation_data"), Negotiated]
    public async Task<IResult> AttestationData([FromQuery(Name = "slot")] string? slot, [FromQuery(Name = "committee_index")] string? committeeIndex, CancellationToken cancellationToken)
    {
        ulong parsedCommitteeIndex = 0;
        if (!TryParseUInt(slot, out ulong parsedSlot) || (committeeIndex is not null && !TryParseUInt(committeeIndex, out parsedCommitteeIndex)))
        {
            return Invalid($"Invalid slot '{slot}' or committee index '{committeeIndex}'.");
        }

        AttestationData data = await ctx.ValidatorApi.ProduceAttestationData(parsedSlot, parsedCommitteeIndex, cancellationToken);
        return ApiResults.Data(ToDto(data));
    }

    /// <summary>beacon-APIs <c>submitPoolAttestationsV2</c>: from Electra the body is a list of <c>SingleAttestation</c>.</summary>
    [HttpPost("/eth/v2/beacon/pool/attestations"), Negotiated]
    public async Task<IResult> SubmitAttestations([FromHeader(Name = ResponseEnvelope.ConsensusVersionHeader)] string? consensusVersion,
        [FromBody] SingleAttestationDto[] items, CancellationToken cancellationToken)
    {
        if (RequireElectraBody(consensusVersion) is { } wrongFork) return wrongFork;
        if (ConvertItems<SingleAttestationDto, SingleAttestation>(items, "Attestation", FromDto, out SingleAttestation[] attestations) is { } malformed) return malformed;
        return Failures(await ctx.ValidatorApi.SubmitAttestations(attestations, cancellationToken), "Some attestations were not published.");
    }

    [HttpPost("/eth/v1/validator/beacon_committee_subscriptions"), Negotiated]
    public async Task<IResult> SubscribeToBeaconCommittees([FromBody] BeaconCommitteeSubscriptionDto[] items, CancellationToken cancellationToken)
    {
        if (ConvertItems<BeaconCommitteeSubscriptionDto, BeaconCommitteeSubscription>(items, "Subscription", FromDto, out BeaconCommitteeSubscription[] subscriptions) is { } malformed) return malformed;
        await ctx.ValidatorApi.SubscribeToBeaconCommittees(subscriptions, cancellationToken);
        return Results.Ok();
    }

    /// <summary>beacon-APIs <c>getAggregatedAttestationV2</c>: the Electra <c>Attestation</c>, versioned by its slot's fork.</summary>
    [HttpGet("/eth/v2/validator/aggregate_attestation"), Negotiated]
    public async Task<IResult> AggregateAttestation([FromQuery(Name = "attestation_data_root")] string? root, [FromQuery(Name = "slot")] string? slot,
        [FromQuery(Name = "committee_index")] string? committeeIndex, CancellationToken cancellationToken)
    {
        if (root is null || !HexConvert.TryParseHash(root, out Hash256? parsedRoot) || !TryParseUInt(slot, out ulong parsedSlot) || !TryParseUInt(committeeIndex, out ulong parsedCommitteeIndex))
        {
            return Invalid($"Invalid attestation data root '{root}', slot '{slot}' or committee index '{committeeIndex}'.");
        }

        Attestation aggregate = await ctx.ValidatorApi.GetAggregateAttestation(parsedSlot, parsedRoot!, parsedCommitteeIndex, cancellationToken);
        BeaconFork fork = ctx.Spec.ForkAtEpoch(ctx.Spec.GetEpoch(parsedSlot));
        return ApiResults.Json(new VersionedDto<AttestationDto>(ResponseEnvelope.ForkName(fork), ToDto(aggregate))).WithConsensusVersion(fork);
    }

    /// <summary>beacon-APIs <c>publishAggregateAndProofsV2</c>: from Electra the body is a list of Electra <c>SignedAggregateAndProof</c>.</summary>
    [HttpPost("/eth/v2/validator/aggregate_and_proofs"), Negotiated]
    public async Task<IResult> SubmitAggregateAndProofs([FromHeader(Name = ResponseEnvelope.ConsensusVersionHeader)] string? consensusVersion,
        [FromBody] SignedAggregateAndProofDto[] items, CancellationToken cancellationToken)
    {
        if (RequireElectraBody(consensusVersion) is { } wrongFork) return wrongFork;
        if (ConvertItems<SignedAggregateAndProofDto, SignedAggregateAndProof>(items, "Aggregate", FromDto, out SignedAggregateAndProof[] aggregates) is { } malformed) return malformed;
        return Failures(await ctx.ValidatorApi.SubmitAggregateAndProofs(aggregates, cancellationToken), "Some aggregates were not published.");
    }

    [HttpGet("/eth/v1/validator/{**path}")]
    [HttpPost("/eth/v1/validator/{**path}")]
    public static Task NotServed(HttpContext c) => Task.FromException(
        new BeaconNodeApiException(BeaconNodeApiError.NotSupported, $"{c.Request.Method} {c.Request.Path} is not served by this node yet."));

    /// <summary>Whether the <c>Eth-Consensus-Version</c> header names a fork whose bodies use the Electra containers.</summary>
    /// <returns>The error response when it does not.</returns>
    private static IResult? RequireElectraBody(string? consensusVersion) => consensusVersion is "electra" or "fulu" or "gloas"
        ? null
        : Invalid($"The {ResponseEnvelope.ConsensusVersionHeader} header must name electra or a later fork.");

    /// <summary>Converts each item of a JSON array.</summary>
    /// <returns>The error response naming the first item <paramref name="fromDto"/> refuses.</returns>
    private static IResult? ConvertItems<TDto, TItem>(TDto[] dtos, string itemName, Func<TDto?, TItem?> fromDto, out TItem[] items) where TItem : class
    {
        items = new TItem[dtos.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (fromDto(dtos[i]) is not { } item) return Invalid($"{itemName} {i} is malformed.");
            items[i] = item;
        }

        return null;
    }

    /// <summary>An empty 200 when nothing was refused, or a 400 naming each refused item.</summary>
    private static IResult Failures(IReadOnlyList<SubmissionFailure> failures, string message)
    {
        if (failures.Count == 0) return Results.Ok();
        FailureDto[] refused = new FailureDto[failures.Count];
        for (int i = 0; i < refused.Length; i++) refused[i] = new FailureDto(failures[i].Index, failures[i].Message);
        IResult body = ApiResults.Json(new IndexedErrorDto(StatusCodes.Status400BadRequest, message, refused));
        return ApiResults.Write(c =>
        {
            c.Response.StatusCode = StatusCodes.Status400BadRequest;
            return body.ExecuteAsync(c);
        });
    }

    private static bool TryParseValidatorIndices(string[] ids, [NotNullWhen(true)] out ulong[]? indices, out string? invalidId)
    {
        indices = new ulong[ids.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            if (!TryParseUInt(ids[i], out indices[i]))
            {
                invalidId = ids[i];
                indices = null;
                return false;
            }
        }

        invalidId = null;
        return true;
    }

    private static IResult Duties<TDuty, TDto>(DutiesResult<TDuty> duties, Func<TDuty, TDto> toDto)
    {
        TDto[] data = new TDto[duties.Duties.Count];
        for (int i = 0; i < data.Length; i++) data[i] = toDto(duties.Duties[i]);
        return ApiResults.Json(new DutiesDto<TDto>(duties.DependentRoot?.ToString(), duties.ExecutionOptimistic, data));
    }

    private static string[] ToStrings(IReadOnlyList<ulong> values)
    {
        string[] strings = new string[values.Count];
        for (int i = 0; i < strings.Length; i++) strings[i] = values[i].ToString();
        return strings;
    }

    private static IResult Invalid(string message) => ApiResults.Error(StatusCodes.Status400BadRequest, message);

    private static bool TryParseUInt(string? value, out ulong result) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static AttestationDataDto ToDto(AttestationData data) =>
        new(data.Slot.ToString(), data.Index.ToString(), data.BeaconBlockRoot!.ToString(), ToDto(data.Source!), ToDto(data.Target!));

    private static CheckpointDto ToDto(Checkpoint checkpoint) => new(checkpoint.Epoch.ToString(), checkpoint.Root!.ToString());

    private static SingleAttestation? FromDto(SingleAttestationDto? dto) =>
        dto is not null && TryParseUInt(dto.CommitteeIndex, out ulong committeeIndex) && TryParseUInt(dto.AttesterIndex, out ulong attesterIndex)
            && FromDto(dto.Data) is { } data && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature)
            ? new SingleAttestation { CommitteeIndex = committeeIndex, AttesterIndex = attesterIndex, Data = data, Signature = signature }
            : null;

    private static AttestationData? FromDto(AttestationDataDto? dto) =>
        dto is not null && TryParseUInt(dto.Slot, out ulong slot) && TryParseUInt(dto.Index, out ulong index) && HexConvert.TryParseHash(dto.BeaconBlockRoot, out Hash256? root)
            && FromDto(dto.Source) is { } source && FromDto(dto.Target) is { } target
            ? new AttestationData { Slot = slot, Index = index, BeaconBlockRoot = root, Source = source, Target = target }
            : null;

    private static Checkpoint? FromDto(CheckpointDto? dto) =>
        dto is not null && TryParseUInt(dto.Epoch, out ulong epoch) && HexConvert.TryParseHash(dto.Root, out Hash256? root)
            ? new Checkpoint { Epoch = epoch, Root = root }
            : null;

    private static BeaconCommitteeSubscription? FromDto(BeaconCommitteeSubscriptionDto? dto) =>
        dto is not null && TryParseUInt(dto.ValidatorIndex, out ulong validatorIndex) && TryParseUInt(dto.CommitteeIndex, out ulong committeeIndex)
            && TryParseUInt(dto.CommitteesAtSlot, out ulong committeesAtSlot) && TryParseUInt(dto.Slot, out ulong slot)
            ? new BeaconCommitteeSubscription(validatorIndex, committeeIndex, committeesAtSlot, slot, dto.IsAggregator)
            : null;

    private static SignedAggregateAndProof? FromDto(SignedAggregateAndProofDto? dto) =>
        dto?.Message is { } message && TryParseUInt(message.AggregatorIndex, out ulong aggregatorIndex) && FromDto(message.Aggregate) is { } aggregate
            && HexConvert.TryParseSignature(message.SelectionProof, out BlsSignature selectionProof) && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature)
            ? new SignedAggregateAndProof
            {
                Message = new AggregateAndProof { AggregatorIndex = aggregatorIndex, Aggregate = aggregate, SelectionProof = selectionProof },
                Signature = signature,
            }
            : null;

    private static Attestation? FromDto(AttestationDto? dto) =>
        dto is not null && HexConvert.TryParseBitList(dto.AggregationBits, (int)Presets.MaxValidatorsPerSlot, out BitArray? aggregationBits) && FromDto(dto.Data) is { } data
            && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature) && HexConvert.TryParseBitVector(dto.CommitteeBits, Presets.MaxCommitteesPerSlot, out BitArray? committeeBits)
            ? new Attestation { AggregationBits = aggregationBits, Data = data, Signature = signature, CommitteeBits = committeeBits }
            : null;

    private static AttestationDto ToDto(Attestation attestation) =>
        new(HexConvert.BitListToHex(attestation.AggregationBits!), ToDto(attestation.Data!), attestation.Signature.ToString(), HexConvert.BitVectorToHex(attestation.CommitteeBits!));

    private sealed record DutiesDto<TDuty>(
        [property: JsonPropertyName("dependent_root"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependentRoot,
        [property: JsonPropertyName("execution_optimistic")] bool ExecutionOptimistic,
        [property: JsonPropertyName("data")] TDuty[] Data);

    private sealed record AttesterDutyDto(
        [property: JsonPropertyName("pubkey")] string PublicKey,
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("committee_index")] string CommitteeIndex,
        [property: JsonPropertyName("committee_length")] string CommitteeLength,
        [property: JsonPropertyName("committees_at_slot")] string CommitteesAtSlot,
        [property: JsonPropertyName("validator_committee_index")] string ValidatorCommitteeIndex,
        [property: JsonPropertyName("slot")] string Slot);

    private sealed record ProposerDutyDto(
        [property: JsonPropertyName("pubkey")] string PublicKey,
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("slot")] string Slot);

    private sealed record SyncDutyDto(
        [property: JsonPropertyName("pubkey")] string PublicKey,
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("validator_sync_committee_indices")] string[] ValidatorSyncCommitteeIndices);

    internal sealed record AttestationDataDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("source")] CheckpointDto Source,
        [property: JsonPropertyName("target")] CheckpointDto Target);

    internal sealed record SingleAttestationDto(
        [property: JsonPropertyName("committee_index")] string CommitteeIndex,
        [property: JsonPropertyName("attester_index")] string AttesterIndex,
        [property: JsonPropertyName("data")] AttestationDataDto Data,
        [property: JsonPropertyName("signature")] string Signature);

    internal sealed record BeaconCommitteeSubscriptionDto(
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("committee_index")] string CommitteeIndex,
        [property: JsonPropertyName("committees_at_slot")] string CommitteesAtSlot,
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("is_aggregator")] bool IsAggregator);

    internal sealed record AttestationDto(
        [property: JsonPropertyName("aggregation_bits")] string AggregationBits,
        [property: JsonPropertyName("data")] AttestationDataDto Data,
        [property: JsonPropertyName("signature")] string Signature,
        [property: JsonPropertyName("committee_bits")] string CommitteeBits);

    internal sealed record AggregateAndProofDto(
        [property: JsonPropertyName("aggregator_index")] string AggregatorIndex,
        [property: JsonPropertyName("aggregate")] AttestationDto Aggregate,
        [property: JsonPropertyName("selection_proof")] string SelectionProof);

    internal sealed record SignedAggregateAndProofDto(
        [property: JsonPropertyName("message")] AggregateAndProofDto Message,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record VersionedDto<TData>(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("data")] TData Data);

    private sealed record FailureDto(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("message")] string Message);

    private sealed record IndexedErrorDto(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("failures")] FailureDto[] Failures);
}
