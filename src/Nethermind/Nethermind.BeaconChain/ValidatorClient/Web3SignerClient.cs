// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Signs through a remote signer that serves the Web3Signer eth2 signing API, <c>POST /api/v1/eth2/sign/{identifier}</c>.</summary>
/// <remarks>
/// The request carries the object, its <c>fork_info</c> and the signing root computed here, which the signer checks against its own.
/// A slashing-protection refusal (412) or an unknown key (404) is a refused <see cref="ValidatorSignerException"/>; any other
/// failure, including a request that outlasts <see cref="RequestTimeout"/>, is not.
/// </remarks>
public sealed class Web3SignerClient(IValidatorClientConfig config, BeaconChainSpec spec) : IValidatorSigner, IDisposable
{
    private readonly string _signUrl = SignUrl(config.RemoteSignerUrl);
    private readonly string _genesisValidatorsRoot = spec.GenesisValidatorsRoot.ToString();

    /// <summary>Sends the signing requests; tests replace it with a client over a scripted handler.</summary>
    internal HttpClient Http { private get; init; } = new();

    /// <summary>
    /// The longest a signing request may take: a third of a slot, so an attestation signed at the attestation deadline
    /// is still published before aggregators collect at two thirds of the slot (phase0/validator.md).
    /// </summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignAttestation(BlsPublicKey publicKey, AttestationData data, CancellationToken cancellationToken) =>
        Sign(publicKey, Request("ATTESTATION", DomainType.BeaconAttester, data.Target!.Epoch, SszRoots.HashTreeRoot(data)) with { Attestation = ToDto(data) },
            $"the attestation of {publicKey} for slot {data.Slot}", cancellationToken);

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignAggregationSlot(BlsPublicKey publicKey, ulong slot, CancellationToken cancellationToken)
    {
        // compute_signing_root(slot, domain): the hash_tree_root of a uint64 is its little-endian chunk.
        Span<byte> slotRoot = stackalloc byte[Hash256.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(slotRoot, slot);
        return Sign(publicKey, Request("AGGREGATION_SLOT", DomainType.SelectionProof, spec.GetEpoch(slot), new Hash256(slotRoot)) with { AggregationSlot = new AggregationSlotDto(slot.ToString()) },
            $"the selection proof of {publicKey} for slot {slot}", cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Sent as <c>AGGREGATE_AND_PROOF_V2</c> with the Electra container, versioned by the fork of the aggregate's slot.</remarks>
    public ValueTask<BlsSignature> SignAggregateAndProof(BlsPublicKey publicKey, AggregateAndProof aggregateAndProof, CancellationToken cancellationToken)
    {
        Attestation aggregate = aggregateAndProof.Aggregate!;
        ulong epoch = spec.GetEpoch(aggregate.Data!.Slot);
        AggregateAndProofDto data = new(aggregateAndProof.AggregatorIndex.ToString(),
            new AttestationDto(HexConvert.BitListToHex(aggregate.AggregationBits!), ToDto(aggregate.Data), aggregate.Signature.ToString(), HexConvert.BitVectorToHex(aggregate.CommitteeBits!)),
            aggregateAndProof.SelectionProof.ToString());
        return Sign(publicKey, Request("AGGREGATE_AND_PROOF_V2", DomainType.AggregateAndProof, epoch, SszRoots.HashTreeRoot(aggregateAndProof))
            with { AggregateAndProof = new VersionedDto(ResponseEnvelope.ForkName(spec.ForkAtEpoch(epoch)).ToUpperInvariant(), data) },
            $"the aggregate of {publicKey} for slot {aggregate.Data.Slot}", cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignSyncCommitteeMessage(BlsPublicKey publicKey, ulong slot, Hash256 beaconBlockRoot, CancellationToken cancellationToken) =>
        Sign(publicKey, Request("SYNC_COMMITTEE_MESSAGE", DomainType.SyncCommittee, spec.GetEpoch(slot), beaconBlockRoot)
            with { SyncCommitteeMessage = new SyncCommitteeMessageDto(beaconBlockRoot.ToString(), slot.ToString()) },
            $"the sync committee message of {publicKey} for slot {slot}", cancellationToken);

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignSyncCommitteeSelectionProof(BlsPublicKey publicKey, ulong slot, ulong subcommitteeIndex, CancellationToken cancellationToken) =>
        Sign(publicKey, Request("SYNC_COMMITTEE_SELECTION_PROOF", DomainType.SyncCommitteeSelectionProof, spec.GetEpoch(slot),
                SszRoots.HashTreeRoot(new SyncAggregatorSelectionData { Slot = slot, SubcommitteeIndex = subcommitteeIndex }))
            with { SyncAggregatorSelectionData = new SyncAggregatorSelectionDataDto(slot.ToString(), subcommitteeIndex.ToString()) },
            $"the sync committee selection proof of {publicKey} for slot {slot} and subcommittee {subcommitteeIndex}", cancellationToken);

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignContributionAndProof(BlsPublicKey publicKey, ContributionAndProof contributionAndProof, CancellationToken cancellationToken)
    {
        SyncCommitteeContribution contribution = contributionAndProof.Contribution!;
        ContributionAndProofDto data = new(contributionAndProof.AggregatorIndex.ToString(), contributionAndProof.SelectionProof.ToString(),
            new SyncCommitteeContributionDto(contribution.Slot.ToString(), contribution.BeaconBlockRoot!.ToString(), contribution.SubcommitteeIndex.ToString(),
                HexConvert.BitVectorToHex(contribution.AggregationBits!), contribution.Signature.ToString()));
        return Sign(publicKey, Request("SYNC_COMMITTEE_CONTRIBUTION_AND_PROOF", DomainType.ContributionAndProof, spec.GetEpoch(contribution.Slot), SszRoots.HashTreeRoot(contributionAndProof))
            with { ContributionAndProof = data },
            $"the contribution of {publicKey} for slot {contribution.Slot} and subcommittee {contribution.SubcommitteeIndex}", cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<BlsSignature> SignRandaoReveal(BlsPublicKey publicKey, ulong epoch, CancellationToken cancellationToken)
    {
        Span<byte> epochRoot = stackalloc byte[Hash256.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(epochRoot, epoch);
        return Sign(publicKey, Request("RANDAO_REVEAL", DomainType.Randao, epoch, new Hash256(epochRoot)) with { RandaoReveal = new RandaoRevealDto(epoch.ToString()) },
            $"the randao reveal of {publicKey} for epoch {epoch}", cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Sent as <c>BLOCK_V2</c> with the <c>block_header</c> Web3Signer takes from Bellatrix on, versioned by the fork of the block's slot.</remarks>
    public ValueTask<BlsSignature> SignBlock(BlsPublicKey publicKey, BeaconBlockHeader blockHeader, CancellationToken cancellationToken)
    {
        ulong epoch = spec.GetEpoch(blockHeader.Slot);
        BlockHeaderDto header = new(blockHeader.Slot.ToString(), blockHeader.ProposerIndex.ToString(), blockHeader.ParentRoot!.ToString(), blockHeader.StateRoot!.ToString(), blockHeader.BodyRoot!.ToString());
        BlockRequestDto block = new(ResponseEnvelope.ForkName(spec.ForkAtEpoch(epoch)).ToUpperInvariant(), header);
        SigningRequest request = Request("BLOCK_V2", DomainType.BeaconProposer, epoch, SszRoots.HashTreeRoot(blockHeader)) with { BeaconBlock = block };
        return Sign(publicKey, request, $"the block of {publicKey} for slot {blockHeader.Slot}", cancellationToken);
    }

    public void Dispose() => Http.Dispose();

    /// <summary>A signing request of <paramref name="type"/> for an object whose root is <paramref name="objectRoot"/>, signed under <paramref name="domainType"/> of <paramref name="epoch"/>.</summary>
    private SigningRequest Request(string type, ReadOnlySpan<byte> domainType, ulong epoch, Hash256 objectRoot) =>
        new(type, ForkInfoAt(epoch), Domains.ComputeSigningRoot(objectRoot, Domains.ComputeDomain(domainType, spec.VersionForEpoch(epoch), spec.GenesisValidatorsRoot)).ToString());

    /// <param name="what">What is signed, for the failure messages.</param>
    private async ValueTask<BlsSignature> Sign(BlsPublicKey publicKey, SigningRequest request, string what, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpRequestMessage message = new(HttpMethod.Post, _signUrl + publicKey)
            {
                Content = JsonContent.Create(request),
                Headers = { Accept = { new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json) } },
            };
            using HttpResponseMessage response = await Http.SendAsync(message, timeout.Token);
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                throw new ValidatorSignerException(response.StatusCode is HttpStatusCode.NotFound
                    ? $"The remote signer does not hold the key {publicKey}."
                    : $"The remote signer's slashing protection refused {what}.", refused: true, innerException: null);

            response.EnsureSuccessStatusCode();
            SigningResponse? body = await response.Content.ReadFromJsonAsync<SigningResponse>(timeout.Token);
            return body?.Signature is { } signature && HexConvert.TryParseSignature(signature, out BlsSignature parsed)
                ? parsed
                : throw new ValidatorSignerException($"The remote signer answered {what} without a signature.", refused: false, innerException: null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ValidatorSignerException($"The remote signer did not sign {what}: {e.Message}", refused: false, e);
        }
    }

    private static string SignUrl(string? remoteSignerUrl) =>
        Uri.TryCreate(remoteSignerUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            ? $"{uri.AbsoluteUri.TrimEnd('/')}/api/v1/eth2/sign/"
            : throw new InvalidConfigurationException($"ValidatorClient.RemoteSignerUrl '{remoteSignerUrl}' is not an absolute http or https URL.", ExitCodes.ForbiddenOptionValue);

    /// <summary>The <c>Fork</c> a state at <paramref name="epoch"/> holds, as <see cref="BeaconChainSpec.Forks"/> schedules it.</summary>
    private ForkInfoDto ForkInfoAt(ulong epoch)
    {
        ForkScheduleEntry[] forks = spec.Forks;
        int current = forks.Length - 1;
        while (forks[current].Epoch > epoch) current--;
        ForkDto fork = new(forks[Math.Max(current - 1, 0)].Version.ToHexString(true), forks[current].Version.ToHexString(true), forks[current].Epoch.ToString());
        return new ForkInfoDto(fork, _genesisValidatorsRoot);
    }

    private static AttestationDataDto ToDto(AttestationData data) =>
        new(data.Slot.ToString(), data.Index.ToString(), data.BeaconBlockRoot!.ToString(), ToDto(data.Source!), ToDto(data.Target!));

    private static CheckpointDto ToDto(Checkpoint checkpoint) => new(checkpoint.Epoch.ToString(), checkpoint.Root!.ToString());

    /// <remarks>Each request carries the one object of its type; the others are left out.</remarks>
    private sealed record SigningRequest(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("fork_info")] ForkInfoDto ForkInfo,
        [property: JsonPropertyName("signingRoot")] string SigningRoot)
    {
        [JsonPropertyName("attestation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AttestationDataDto? Attestation { get; init; }

        [JsonPropertyName("aggregation_slot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AggregationSlotDto? AggregationSlot { get; init; }

        [JsonPropertyName("aggregate_and_proof"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public VersionedDto? AggregateAndProof { get; init; }

        [JsonPropertyName("sync_committee_message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SyncCommitteeMessageDto? SyncCommitteeMessage { get; init; }

        [JsonPropertyName("sync_aggregator_selection_data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SyncAggregatorSelectionDataDto? SyncAggregatorSelectionData { get; init; }

        [JsonPropertyName("contribution_and_proof"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ContributionAndProofDto? ContributionAndProof { get; init; }

        [JsonPropertyName("randao_reveal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RandaoRevealDto? RandaoReveal { get; init; }

        [JsonPropertyName("beacon_block"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BlockRequestDto? BeaconBlock { get; init; }
    }

    private sealed record ForkInfoDto(
        [property: JsonPropertyName("fork")] ForkDto Fork,
        [property: JsonPropertyName("genesis_validators_root")] string GenesisValidatorsRoot);

    private sealed record ForkDto(
        [property: JsonPropertyName("previous_version")] string PreviousVersion,
        [property: JsonPropertyName("current_version")] string CurrentVersion,
        [property: JsonPropertyName("epoch")] string Epoch);

    private sealed record AttestationDataDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("source")] CheckpointDto Source,
        [property: JsonPropertyName("target")] CheckpointDto Target);

    private sealed record CheckpointDto(
        [property: JsonPropertyName("epoch")] string Epoch,
        [property: JsonPropertyName("root")] string Root);

    private sealed record AggregationSlotDto([property: JsonPropertyName("slot")] string Slot);

    private sealed record VersionedDto(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("data")] AggregateAndProofDto Data);

    private sealed record AggregateAndProofDto(
        [property: JsonPropertyName("aggregator_index")] string AggregatorIndex,
        [property: JsonPropertyName("aggregate")] AttestationDto Aggregate,
        [property: JsonPropertyName("selection_proof")] string SelectionProof);

    private sealed record AttestationDto(
        [property: JsonPropertyName("aggregation_bits")] string AggregationBits,
        [property: JsonPropertyName("data")] AttestationDataDto Data,
        [property: JsonPropertyName("signature")] string Signature,
        [property: JsonPropertyName("committee_bits")] string CommitteeBits);

    private sealed record SyncCommitteeMessageDto(
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("slot")] string Slot);

    private sealed record SyncAggregatorSelectionDataDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("subcommittee_index")] string SubcommitteeIndex);

    private sealed record ContributionAndProofDto(
        [property: JsonPropertyName("aggregator_index")] string AggregatorIndex,
        [property: JsonPropertyName("selection_proof")] string SelectionProof,
        [property: JsonPropertyName("contribution")] SyncCommitteeContributionDto Contribution);

    private sealed record SyncCommitteeContributionDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("beacon_block_root")] string BeaconBlockRoot,
        [property: JsonPropertyName("subcommittee_index")] string SubcommitteeIndex,
        [property: JsonPropertyName("aggregation_bits")] string AggregationBits,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record RandaoRevealDto([property: JsonPropertyName("epoch")] string Epoch);

    private sealed record BlockRequestDto(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("block_header")] BlockHeaderDto BlockHeader);

    private sealed record BlockHeaderDto(
        [property: JsonPropertyName("slot")] string Slot,
        [property: JsonPropertyName("proposer_index")] string ProposerIndex,
        [property: JsonPropertyName("parent_root")] string ParentRoot,
        [property: JsonPropertyName("state_root")] string StateRoot,
        [property: JsonPropertyName("body_root")] string BodyRoot);

    private sealed record SigningResponse([property: JsonPropertyName("signature")] string? Signature);
}
