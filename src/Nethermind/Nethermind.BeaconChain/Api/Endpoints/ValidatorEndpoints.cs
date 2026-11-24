// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core;
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
        app.MapPost("/eth/v1/validator/beacon_committee_subscriptions", c => SubscribeToBeaconCommittees(c, ctx));
        app.MapGet("/eth/v2/validator/aggregate_attestation", c => ContentNegotiation.JsonOnly(c, ctx, AggregateAttestation));
        app.MapPost("/eth/v2/validator/aggregate_and_proofs", c => SubmitAggregateAndProofs(c, ctx));
        app.MapPost("/eth/v1/validator/sync_committee_subscriptions", c => SubscribeToSyncCommittees(c, ctx));
        app.MapPost("/eth/v1/beacon/pool/sync_committees", c => SubmitSyncCommitteeMessages(c, ctx));
        app.MapGet("/eth/v1/validator/sync_committee_contribution", c => ContentNegotiation.JsonOnly(c, ctx, ProduceContribution));
        app.MapPost("/eth/v1/validator/contribution_and_proofs", c => SubmitContributionAndProofs(c, ctx));
        app.MapPost("/eth/v1/validator/prepare_beacon_proposer", c => PrepareBeaconProposers(c, ctx));
        app.MapGet("/eth/v3/validator/blocks/{slot}", (HttpContext c, string slot) => ProduceBlock(c, slot, ctx));
        app.MapPost("/eth/v2/beacon/blocks", c => PublishBlock(c, ctx));
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
        if (!await RequireElectraBody(c)) return;
        if (await ReadItems<SingleAttestationDto, SingleAttestation>(c, "Attestation", FromDto) is not { } attestations) return;
        await WriteFailures(c, await ctx.ValidatorApi.SubmitAttestations(attestations, c.RequestAborted), "Some attestations were not published.");
    }

    private static async Task SubscribeToBeaconCommittees(HttpContext c, BeaconApiContext ctx)
    {
        if (await ReadItems<BeaconCommitteeSubscriptionDto, BeaconCommitteeSubscription>(c, "Subscription", FromDto) is not { } subscriptions) return;
        await ctx.ValidatorApi.SubscribeToBeaconCommittees(subscriptions, c.RequestAborted);
    }

    /// <summary>beacon-APIs <c>getAggregatedAttestationV2</c>: the Electra <c>Attestation</c>, versioned by its slot's fork.</summary>
    private static async Task AggregateAttestation(HttpContext c, BeaconApiContext ctx)
    {
        StringValues root = c.Request.Query["attestation_data_root"];
        StringValues slot = c.Request.Query["slot"];
        StringValues committeeIndex = c.Request.Query["committee_index"];
        if (!HexConvert.TryParseHash(root.ToString(), out Hash256? parsedRoot) || !TryParseUInt(slot, out ulong parsedSlot) || !TryParseUInt(committeeIndex, out ulong parsedCommitteeIndex))
        {
            await WriteInvalid(c, $"Invalid attestation data root '{root}', slot '{slot}' or committee index '{committeeIndex}'.");
            return;
        }

        Attestation aggregate = await ctx.ValidatorApi.GetAggregateAttestation(parsedSlot, parsedRoot!, parsedCommitteeIndex, c.RequestAborted);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, parsedSlot);
        c.Response.ContentType = ContentNegotiation.Json;
        await JsonSerializer.SerializeAsync(c.Response.Body, new VersionedDto<AttestationDto>(c.Response.Headers[ResponseEnvelope.ConsensusVersionHeader].ToString(), ToDto(aggregate)),
            BeaconApiJson.Options, c.RequestAborted);
    }

    /// <summary>beacon-APIs <c>publishAggregateAndProofsV2</c>: from Electra the body is a list of Electra <c>SignedAggregateAndProof</c>.</summary>
    private static async Task SubmitAggregateAndProofs(HttpContext c, BeaconApiContext ctx)
    {
        if (!await RequireElectraBody(c)) return;
        if (await ReadItems<SignedAggregateAndProofDto, SignedAggregateAndProof>(c, "Aggregate", FromDto) is not { } aggregates) return;
        await WriteFailures(c, await ctx.ValidatorApi.SubmitAggregateAndProofs(aggregates, c.RequestAborted), "Some aggregates were not published.");
    }

    private static async Task SubscribeToSyncCommittees(HttpContext c, BeaconApiContext ctx)
    {
        if (await ReadItems<SyncCommitteeSubscriptionDto, SyncCommitteeSubscription>(c, "Subscription", FromDto) is not { } subscriptions) return;
        await ctx.ValidatorApi.SubscribeToSyncCommittees(subscriptions, c.RequestAborted);
    }

    private static async Task SubmitSyncCommitteeMessages(HttpContext c, BeaconApiContext ctx)
    {
        if (await ReadItems<SyncCommitteeMessageDto, SyncCommitteeMessage>(c, "Sync committee message", FromDto) is not { } messages) return;
        await WriteFailures(c, await ctx.ValidatorApi.SubmitSyncCommitteeMessages(messages, c.RequestAborted), "Some sync committee messages were not published.");
    }

    private static async Task ProduceContribution(HttpContext c, BeaconApiContext ctx)
    {
        StringValues slot = c.Request.Query["slot"];
        StringValues subcommitteeIndex = c.Request.Query["subcommittee_index"];
        StringValues root = c.Request.Query["beacon_block_root"];
        if (!TryParseUInt(slot, out ulong parsedSlot) || !TryParseUInt(subcommitteeIndex, out ulong parsedSubcommitteeIndex) || !HexConvert.TryParseHash(root.ToString(), out Hash256? parsedRoot))
        {
            await WriteInvalid(c, $"Invalid slot '{slot}', subcommittee index '{subcommitteeIndex}' or beacon block root '{root}'.");
            return;
        }

        SyncCommitteeContribution contribution = await ctx.ValidatorApi.ProduceSyncCommitteeContribution(parsedSlot, parsedSubcommitteeIndex, parsedRoot!, c.RequestAborted);
        await BeaconApiJson.WriteDataAsync(c, ToDto(contribution), c.RequestAborted);
    }

    private static async Task SubmitContributionAndProofs(HttpContext c, BeaconApiContext ctx)
    {
        if (await ReadItems<SignedContributionAndProofDto, SignedContributionAndProof>(c, "Contribution", FromDto) is not { } contributions) return;
        await WriteFailures(c, await ctx.ValidatorApi.SubmitContributionAndProofs(contributions, c.RequestAborted), "Some contributions were not published.");
    }

    private static async Task PrepareBeaconProposers(HttpContext c, BeaconApiContext ctx)
    {
        if (await ReadItems<ProposerPreparationDto, ProposerPreparation>(c, "Preparation", FromDto) is not { } preparations) return;
        await ctx.ValidatorApi.PrepareBeaconProposers(preparations, c.RequestAborted);
    }

    /// <summary>beacon-APIs <c>produceBlockV3</c>: always a full block with the local execution layer's payload, in JSON or SSZ.</summary>
    /// <remarks>
    /// <c>builder_boost_factor</c> is checked and ignored, as the node has no builder. <c>skip_randao_verification</c> is not honoured, so the
    /// reveal is always checked.
    /// </remarks>
    private static async Task ProduceBlock(HttpContext c, string slot, BeaconApiContext ctx)
    {
        StringValues randaoReveal = c.Request.Query["randao_reveal"];
        StringValues graffiti = c.Request.Query["graffiti"];
        StringValues builderBoostFactor = c.Request.Query["builder_boost_factor"];
        Hash256? parsedGraffiti = Hash256.Zero;
        if (!TryParseUInt(slot, out ulong parsedSlot) || !HexConvert.TryParseSignature(randaoReveal.ToString(), out BlsSignature parsedReveal)
            || (graffiti.Count > 0 && !HexConvert.TryParseHash(graffiti.ToString(), out parsedGraffiti)) || (builderBoostFactor.Count > 0 && !TryParseUInt(builderBoostFactor, out _)))
        {
            await WriteInvalid(c, $"Invalid slot '{slot}', randao reveal '{randaoReveal}', graffiti '{graffiti}' or builder boost factor '{builderBoostFactor}'.");
            return;
        }

        ContentNegotiation.ResponseFormat? format = ContentNegotiation.Negotiate(c, sszSupported: true);
        if (format is null)
        {
            await ContentNegotiation.WriteNotAcceptable(c);
            return;
        }

        ProducedBlock produced = await ctx.ValidatorApi.ProduceBlock(parsedSlot, parsedReveal, parsedGraffiti!, c.RequestAborted);
        ResponseEnvelope.ApplyConsensusVersionHeader(c, ctx.Spec, parsedSlot);
        c.Response.Headers[ExecutionPayloadBlindedHeader] = "false";
        c.Response.Headers[ExecutionPayloadValueHeader] = produced.ExecutionPayloadValue.ToString();
        c.Response.Headers[ConsensusBlockValueHeader] = produced.ConsensusBlockValue.ToString();
        if (format == ContentNegotiation.ResponseFormat.Ssz)
        {
            BlockContentsWire contents = new() { Block = produced.Block, KzgProofs = new SszKzgCommitment[produced.KzgProofs.Count], Blobs = new SszBlob[produced.Blobs.Count] };
            for (int i = 0; i < contents.KzgProofs.Length; i++) contents.KzgProofs[i] = SszKzgCommitment.FromSpan(produced.KzgProofs[i]);
            for (int i = 0; i < contents.Blobs.Length; i++) contents.Blobs[i] = new SszBlob { Bytes = produced.Blobs[i] };
            c.Response.ContentType = ContentNegotiation.OctetStream;
            await c.Response.Body.WriteAsync(BlockContentsWire.Encode(contents), c.RequestAborted);
            return;
        }

        c.Response.ContentType = ContentNegotiation.Json;
        await using BeaconJsonStream stream = new(c.Response.BodyWriter, c.RequestAborted);
        Utf8JsonWriter w = stream.Writer;
        w.WriteStartObject();
        w.WriteString("version", c.Response.Headers[ResponseEnvelope.ConsensusVersionHeader].ToString());
        w.WriteBoolean("execution_payload_blinded", false);
        w.WriteString("execution_payload_value", produced.ExecutionPayloadValue.ToString());
        w.WriteString("consensus_block_value", produced.ConsensusBlockValue.ToString());
        w.WritePropertyName("data");
        w.WriteStartObject();
        w.WritePropertyName("block");
        BeaconJsonWriter.WriteBeaconBlock(w, produced.Block);
        await WriteHexArray(stream, "kzg_proofs", produced.KzgProofs);
        await WriteHexArray(stream, "blobs", produced.Blobs);
        w.WriteEndObject();
        w.WriteEndObject();
        await stream.FlushAsync();
    }

    /// <summary>beacon-APIs <c>publishBlockV2</c> of a Fulu <c>SignedBlockContents</c>, in JSON or SSZ, with <c>broadcast_validation</c> <c>gossip</c>, its default, only.</summary>
    private static async Task PublishBlock(HttpContext c, BeaconApiContext ctx)
    {
        string version = c.Request.Headers[ResponseEnvelope.ConsensusVersionHeader].ToString();
        StringValues broadcastValidation = c.Request.Query["broadcast_validation"];
        if (version == "gloas") throw new BeaconNodeApiException(BeaconNodeApiError.NotSupported, "Gloas blocks are not served by this node yet.");
        if (version != "fulu")
        {
            await WriteInvalid(c, $"The {ResponseEnvelope.ConsensusVersionHeader} header must name fulu.");
            return;
        }

        if (broadcastValidation.Count > 0 && broadcastValidation.ToString() != "gossip")
        {
            await WriteInvalid(c, $"Broadcast validation '{broadcastValidation}' is not served; only gossip is.");
            return;
        }

        if (await ReadSignedBlockContents(c) is not { } contents) return;
        await ctx.ValidatorApi.PublishBlock(contents, c.RequestAborted);
    }

    /// <summary>Reads a Fulu <c>SignedBlockContents</c> from a JSON or SSZ body, writing the error response itself when it cannot.</summary>
    private static async Task<SignedBlockContents?> ReadSignedBlockContents(HttpContext c)
    {
        bool ssz = ContentNegotiation.IsAcceptableContentType(c, ContentNegotiation.OctetStream);
        if (!ssz && !ContentNegotiation.IsAcceptableContentType(c, ContentNegotiation.Json))
        {
            await ContentNegotiation.WriteUnsupportedMediaType(c, $"{ContentNegotiation.Json}' or '{ContentNegotiation.OctetStream}");
            return null;
        }

        try
        {
            if (ssz)
            {
                using MemoryStream body = new();
                await c.Request.Body.CopyToAsync(body, c.RequestAborted);
                SignedBlockContentsWire.Decode(body.ToArray(), out SignedBlockContentsWire wire);
                byte[][] blobBytes = new byte[wire.Blobs!.Length][];
                for (int i = 0; i < blobBytes.Length; i++) blobBytes[i] = wire.Blobs[i].Bytes!;
                byte[][] proofBytes = new byte[wire.KzgProofs!.Length][];
                for (int i = 0; i < proofBytes.Length; i++) proofBytes[i] = wire.KzgProofs[i].AsSpan().ToArray();
                return new SignedBlockContents(wire.SignedBlock!, blobBytes, proofBytes);
            }

            using JsonDocument document = await JsonDocument.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted);
            (SignedBeaconBlock block, byte[][] kzgProofs, byte[][] blobs) = BeaconJsonReader.ReadSignedBlockContents(document.RootElement);
            // The SSZ codec checks the list limits the JSON form leaves open.
            SignedBeaconBlock.Decode(SignedBeaconBlock.Encode(block), out SignedBeaconBlock checkedBlock);
            return new SignedBlockContents(checkedBlock, blobs, kzgProofs);
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            await WriteInvalid(c, $"Malformed block contents: {e.Message}");
        }
        catch (BadHttpRequestException e)
        {
            await ApiErrors.Write(c, e.StatusCode, e.Message, c.RequestAborted);
        }

        return null;
    }

    private static async Task WriteHexArray(BeaconJsonStream stream, string name, IReadOnlyList<byte[]> items)
    {
        stream.Writer.WriteStartArray(name);
        foreach (byte[] item in items)
        {
            BeaconJsonWriter.WriteHexValue(stream.Writer, item);
            await stream.CheckpointAsync();
        }

        stream.Writer.WriteEndArray();
    }

    /// <summary>Whether the <c>Eth-Consensus-Version</c> header names a fork whose bodies use the Electra containers, writing the error response itself when not.</summary>
    private static async Task<bool> RequireElectraBody(HttpContext c)
    {
        if (c.Request.Headers[ResponseEnvelope.ConsensusVersionHeader].ToString() is "electra" or "fulu" or "gloas") return true;
        await WriteInvalid(c, $"The {ResponseEnvelope.ConsensusVersionHeader} header must name electra or a later fork.");
        return false;
    }

    /// <summary>Reads a JSON array and converts each item, writing the error response itself when it cannot.</summary>
    private static async Task<TItem[]?> ReadItems<TDto, TItem>(HttpContext c, string itemName, Func<TDto?, TItem?> fromDto) where TItem : class
    {
        (bool read, TDto[]? dtos) = await BeaconStatesEndpoints.TryReadJsonBody<TDto[]>(c, bodyRequired: true);
        if (!read) return null;
        TItem[] items = new TItem[dtos!.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (fromDto(dtos[i]) is not { } item)
            {
                await WriteInvalid(c, $"{itemName} {i} is malformed.");
                return null;
            }

            items[i] = item;
        }

        return items;
    }

    private static async Task WriteFailures(HttpContext c, IReadOnlyList<SubmissionFailure> failures, string message)
    {
        if (failures.Count == 0) return;
        FailureDto[] refused = new FailureDto[failures.Count];
        for (int i = 0; i < refused.Length; i++) refused[i] = new FailureDto(failures[i].Index, failures[i].Message);
        c.Response.StatusCode = StatusCodes.Status400BadRequest;
        c.Response.ContentType = ContentNegotiation.Json;
        await JsonSerializer.SerializeAsync(c.Response.Body, new IndexedErrorDto(StatusCodes.Status400BadRequest, message, refused), BeaconApiJson.Options, c.RequestAborted);
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

    private static SyncCommitteeSubscription? FromDto(SyncCommitteeSubscriptionDto? dto)
    {
        if (dto?.SyncCommitteeIndices is null || !TryParseUInt(dto.ValidatorIndex, out ulong validatorIndex) || !TryParseUInt(dto.UntilEpoch, out ulong untilEpoch)) return null;
        ulong[] positions = new ulong[dto.SyncCommitteeIndices.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            if (!TryParseUInt(dto.SyncCommitteeIndices[i], out positions[i])) return null;
        }

        return new SyncCommitteeSubscription(validatorIndex, positions, untilEpoch);
    }

    private static SyncCommitteeMessage? FromDto(SyncCommitteeMessageDto? dto) =>
        dto is not null && TryParseUInt(dto.Slot, out ulong slot) && HexConvert.TryParseHash(dto.BeaconBlockRoot, out Hash256? root)
            && TryParseUInt(dto.ValidatorIndex, out ulong validatorIndex) && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature)
            ? new SyncCommitteeMessage { Slot = slot, BeaconBlockRoot = root, ValidatorIndex = validatorIndex, Signature = signature }
            : null;

    private static SignedContributionAndProof? FromDto(SignedContributionAndProofDto? dto) =>
        dto?.Message is { } message && TryParseUInt(message.AggregatorIndex, out ulong aggregatorIndex) && FromDto(message.Contribution) is { } contribution
            && HexConvert.TryParseSignature(message.SelectionProof, out BlsSignature selectionProof) && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature)
            ? new SignedContributionAndProof
            {
                Message = new ContributionAndProof { AggregatorIndex = aggregatorIndex, Contribution = contribution, SelectionProof = selectionProof },
                Signature = signature,
            }
            : null;

    private static ProposerPreparation? FromDto(ProposerPreparationDto? dto) =>
        dto is not null && TryParseUInt(dto.ValidatorIndex, out ulong validatorIndex) && HexConvert.TryParseAddress(dto.FeeRecipient, out Address? feeRecipient)
            ? new ProposerPreparation(validatorIndex, feeRecipient)
            : null;

    private static SyncCommitteeContribution? FromDto(SyncCommitteeContributionDto? dto) =>
        dto is not null && TryParseUInt(dto.Slot, out ulong slot) && HexConvert.TryParseHash(dto.BeaconBlockRoot, out Hash256? root)
            && TryParseUInt(dto.SubcommitteeIndex, out ulong subcommitteeIndex)
            && HexConvert.TryParseBitVector(dto.AggregationBits, SyncCommitteeContribution.SubcommitteeSize, out BitArray? aggregationBits)
            && HexConvert.TryParseSignature(dto.Signature, out BlsSignature signature)
            ? new SyncCommitteeContribution { Slot = slot, BeaconBlockRoot = root, SubcommitteeIndex = subcommitteeIndex, AggregationBits = aggregationBits, Signature = signature }
            : null;

    private static SyncCommitteeContributionDto ToDto(SyncCommitteeContribution contribution) =>
        new(contribution.Slot.ToString(), contribution.BeaconBlockRoot!.ToString(), contribution.SubcommitteeIndex.ToString(),
            HexConvert.BitVectorToHex(contribution.AggregationBits!), contribution.Signature.ToString());

    private const string ExecutionPayloadBlindedHeader = "Eth-Execution-Payload-Blinded";
    private const string ExecutionPayloadValueHeader = "Eth-Execution-Payload-Value";
    private const string ConsensusBlockValueHeader = "Eth-Consensus-Block-Value";

    private sealed record ProposerPreparationDto(
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("fee_recipient")] string FeeRecipient);

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

    private sealed record BeaconCommitteeSubscriptionDto(
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("committee_index")] string CommitteeIndex,
        [property: JsonPropertyName("committees_at_slot")] string CommitteesAtSlot,
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("is_aggregator")] bool IsAggregator);

    private sealed record AttestationDto(
        [property: JsonPropertyName("aggregation_bits")] string AggregationBits,
        [property: JsonPropertyName("data")] AttestationDataDto Data,
        [property: JsonPropertyName("signature")] string Signature,
        [property: JsonPropertyName("committee_bits")] string CommitteeBits);

    private sealed record AggregateAndProofDto(
        [property: JsonPropertyName("aggregator_index")] string AggregatorIndex,
        [property: JsonPropertyName("aggregate")] AttestationDto Aggregate,
        [property: JsonPropertyName("selection_proof")] string SelectionProof);

    private sealed record SignedAggregateAndProofDto(
        [property: JsonPropertyName("message")] AggregateAndProofDto Message,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record SyncCommitteeSubscriptionDto(
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("sync_committee_indices")] string[] SyncCommitteeIndices,
        [property: JsonPropertyName("until_epoch")] string UntilEpoch);

    private sealed record SyncCommitteeMessageDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("validator_index")] string ValidatorIndex,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record SyncCommitteeContributionDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("subcommittee_index")] string SubcommitteeIndex,
        [property: JsonPropertyName("aggregation_bits")] string AggregationBits,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record ContributionAndProofDto(
        [property: JsonPropertyName("aggregator_index")] string AggregatorIndex,
        [property: JsonPropertyName("selection_proof")] string SelectionProof,
        [property: JsonPropertyName("contribution")] SyncCommitteeContributionDto Contribution);

    private sealed record SignedContributionAndProofDto(
        [property: JsonPropertyName("message")] ContributionAndProofDto Message,
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
