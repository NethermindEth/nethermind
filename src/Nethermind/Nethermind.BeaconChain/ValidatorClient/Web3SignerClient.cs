// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public async ValueTask<BlsSignature> SignAttestation(BlsPublicKey publicKey, AttestationData data, CancellationToken cancellationToken)
    {
        ulong targetEpoch = data.Target!.Epoch;
        Hash256 domain = Domains.ComputeDomain(DomainType.BeaconAttester, spec.VersionForEpoch(targetEpoch), spec.GenesisValidatorsRoot);
        SigningRequest request = new("ATTESTATION", ForkInfoAt(targetEpoch), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain).ToString(), ToDto(data));
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
                    : $"The remote signer's slashing protection refused the attestation of {publicKey} for slot {data.Slot}.", refused: true, innerException: null);

            response.EnsureSuccessStatusCode();
            SigningResponse? body = await response.Content.ReadFromJsonAsync<SigningResponse>(timeout.Token);
            return body?.Signature is { } signature && HexConvert.TryParseSignature(signature, out BlsSignature parsed)
                ? parsed
                : throw new ValidatorSignerException($"The remote signer answered the attestation of {publicKey} without a signature.", refused: false, innerException: null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ValidatorSignerException($"The remote signer did not sign the attestation of {publicKey} for slot {data.Slot}: {e.Message}", refused: false, e);
        }
    }

    public void Dispose() => Http.Dispose();

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

    private sealed record SigningRequest(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("fork_info")] ForkInfoDto ForkInfo,
        [property: JsonPropertyName("signingRoot")] string SigningRoot,
        [property: JsonPropertyName("attestation")] AttestationDataDto Attestation);

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

    private sealed record SigningResponse([property: JsonPropertyName("signature")] string? Signature);
}
