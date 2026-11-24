// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
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
internal static class ValidatorEndpoints
{
    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapPost("/eth/v1/validator/duties/attester/{epoch}", (HttpContext c, string epoch) => ContentNegotiation.JsonOnly(c, ctx, epoch, AttesterDuties));
        app.MapGet("/eth/v2/validator/duties/proposer/{epoch}", (HttpContext c, string epoch) => ContentNegotiation.JsonOnly(c, ctx, epoch, ProposerDuties));
        app.MapPost("/eth/v1/validator/duties/sync/{epoch}", (HttpContext c, string epoch) => ContentNegotiation.JsonOnly(c, ctx, epoch, SyncDuties));
        app.MapGet("/eth/v1/validator/attestation_data", c => ContentNegotiation.JsonOnly(c, ctx, AttestationData));
        app.MapPost("/eth/v2/beacon/pool/attestations", c => SubmitAttestations(c, ctx));
        app.MapMethods("/eth/v1/validator/{**path}", ["GET", "POST"], c => Task.FromException(
            new BeaconNodeApiException(BeaconNodeApiError.NotSupported, $"{c.Request.Method} {c.Request.Path} is not served by this node yet.")));
    }

    private static async Task AttesterDuties(HttpContext c, string epoch, BeaconApiContext ctx)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch))
        {
            await WriteInvalid(c, $"Invalid epoch '{epoch}'.");
            return;
        }

        if (await ReadValidatorIndices(c) is not { } indices) return;
        DutiesResult<AttesterDuty> duties = await ctx.ValidatorApi.GetAttesterDuties(parsedEpoch, indices, c.RequestAborted);
        await WriteDuties(c, duties, static duty => new AttesterDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), duty.CommitteeIndex.ToString(),
            duty.CommitteeLength.ToString(), duty.CommitteesAtSlot.ToString(), duty.ValidatorCommitteeIndex.ToString(), duty.Slot.ToString()));
    }

    private static async Task ProposerDuties(HttpContext c, string epoch, BeaconApiContext ctx)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch))
        {
            await WriteInvalid(c, $"Invalid epoch '{epoch}'.");
            return;
        }

        DutiesResult<ProposerDuty> duties = await ctx.ValidatorApi.GetProposerDuties(parsedEpoch, c.RequestAborted);
        await WriteDuties(c, duties, static duty => new ProposerDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), duty.Slot.ToString()));
    }

    private static async Task SyncDuties(HttpContext c, string epoch, BeaconApiContext ctx)
    {
        if (!TryParseUInt(epoch, out ulong parsedEpoch))
        {
            await WriteInvalid(c, $"Invalid epoch '{epoch}'.");
            return;
        }

        if (await ReadValidatorIndices(c) is not { } indices) return;
        DutiesResult<SyncDuty> duties = await ctx.ValidatorApi.GetSyncDuties(parsedEpoch, indices, c.RequestAborted);
        await WriteDuties(c, duties, static duty => new SyncDutyDto(duty.PublicKey.ToString(), duty.ValidatorIndex.ToString(), ToStrings(duty.ValidatorSyncCommitteeIndices)));
    }

    private static async Task AttestationData(HttpContext c, BeaconApiContext ctx)
    {
        StringValues slot = c.Request.Query["slot"];
        StringValues committeeIndex = c.Request.Query["committee_index"];
        ulong parsedCommitteeIndex = 0;
        if (!TryParseUInt(slot, out ulong parsedSlot) || (committeeIndex.Count > 0 && !TryParseUInt(committeeIndex, out parsedCommitteeIndex)))
        {
            await WriteInvalid(c, $"Invalid slot '{slot}' or committee index '{committeeIndex}'.");
            return;
        }

        AttestationData data = await ctx.ValidatorApi.ProduceAttestationData(parsedSlot, parsedCommitteeIndex, c.RequestAborted);
        await BeaconApiJson.WriteDataAsync(c, ToDto(data), c.RequestAborted);
    }

    /// <summary>beacon-APIs <c>submitPoolAttestationsV2</c>: from Electra the body is a list of <c>SingleAttestation</c>.</summary>
    private static async Task SubmitAttestations(HttpContext c, BeaconApiContext ctx)
    {
        if (c.Request.Headers[ResponseEnvelope.ConsensusVersionHeader].ToString() is not ("electra" or "fulu" or "gloas"))
        {
            await WriteInvalid(c, $"The {ResponseEnvelope.ConsensusVersionHeader} header must name electra or a later fork.");
            return;
        }

        (bool read, SingleAttestationDto[]? items) = await BeaconStatesEndpoints.TryReadJsonBody<SingleAttestationDto[]>(c, bodyRequired: true);
        if (!read) return;
        SingleAttestation[] attestations = new SingleAttestation[items!.Length];
        for (int i = 0; i < attestations.Length; i++)
        {
            if (FromDto(items[i]) is not { } attestation)
            {
                await WriteInvalid(c, $"Attestation {i} is malformed.");
                return;
            }

            attestations[i] = attestation;
        }

        IReadOnlyList<SubmissionFailure> failures = await ctx.ValidatorApi.SubmitAttestations(attestations, c.RequestAborted);
        if (failures.Count > 0)
        {
            FailureDto[] refused = new FailureDto[failures.Count];
            for (int i = 0; i < refused.Length; i++) refused[i] = new FailureDto(failures[i].Index, failures[i].Message);
            c.Response.StatusCode = StatusCodes.Status400BadRequest;
            c.Response.ContentType = ContentNegotiation.Json;
            await JsonSerializer.SerializeAsync(c.Response.Body, new IndexedErrorDto(StatusCodes.Status400BadRequest, "Some attestations were not published.", refused),
                BeaconApiJson.Options, c.RequestAborted);
        }
    }

    /// <summary>Reads a JSON array of validator indices, writing the error response itself when it cannot.</summary>
    private static async Task<ulong[]?> ReadValidatorIndices(HttpContext c)
    {
        (bool read, string[]? ids) = await BeaconStatesEndpoints.TryReadJsonBody<string[]>(c, bodyRequired: true);
        if (!read) return null;
        ulong[] indices = new ulong[ids!.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            if (!TryParseUInt(ids[i], out indices[i]))
            {
                await WriteInvalid(c, $"Invalid validator index '{ids[i]}'.");
                return null;
            }
        }

        return indices;
    }

    private static Task WriteDuties<TDuty, TDto>(HttpContext c, DutiesResult<TDuty> duties, Func<TDuty, TDto> toDto)
    {
        TDto[] data = new TDto[duties.Duties.Count];
        for (int i = 0; i < data.Length; i++) data[i] = toDto(duties.Duties[i]);
        c.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(c.Response.Body, new DutiesDto<TDto>(duties.DependentRoot?.ToString(), duties.ExecutionOptimistic, data),
            BeaconApiJson.Options, c.RequestAborted);
    }

    private static string[] ToStrings(IReadOnlyList<ulong> values)
    {
        string[] strings = new string[values.Count];
        for (int i = 0; i < strings.Length; i++) strings[i] = values[i].ToString();
        return strings;
    }

    private static Task WriteInvalid(HttpContext c, string message) => ApiErrors.Write(c, StatusCodes.Status400BadRequest, message, c.RequestAborted);

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

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    private sealed record AttestationDataDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("source")] CheckpointDto Source,
        [property: JsonPropertyName("target")] CheckpointDto Target);

    private sealed record SingleAttestationDto(
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
