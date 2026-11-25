// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nethermind.BeaconChain.Api.Common;
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
        if (consensusVersion is not ("electra" or "fulu" or "gloas"))
        {
            return Invalid($"The {ResponseEnvelope.ConsensusVersionHeader} header must name electra or a later fork.");
        }

        SingleAttestation[] attestations = new SingleAttestation[items.Length];
        for (int i = 0; i < attestations.Length; i++)
        {
            if (FromDto(items[i]) is not { } attestation) return Invalid($"Attestation {i} is malformed.");
            attestations[i] = attestation;
        }

        IReadOnlyList<SubmissionFailure> failures = await ctx.ValidatorApi.SubmitAttestations(attestations, cancellationToken);
        if (failures.Count == 0) return Results.Ok();
        FailureDto[] refused = new FailureDto[failures.Count];
        for (int i = 0; i < refused.Length; i++) refused[i] = new FailureDto(failures[i].Index, failures[i].Message);
        IResult body = ApiResults.Json(new IndexedErrorDto(StatusCodes.Status400BadRequest, "Some attestations were not published.", refused));
        return ApiResults.Write(c =>
        {
            c.Response.StatusCode = StatusCodes.Status400BadRequest;
            return body.ExecuteAsync(c);
        });
    }

    [HttpGet("/eth/v1/validator/{**path}")]
    [HttpPost("/eth/v1/validator/{**path}")]
    public static Task NotServed(HttpContext c) => Task.FromException(
        new BeaconNodeApiException(BeaconNodeApiError.NotSupported, $"{c.Request.Method} {c.Request.Path} is not served by this node yet."));

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

    private sealed record FailureDto(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("message")] string Message);

    private sealed record IndexedErrorDto(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("failures")] FailureDto[] Failures);
}
