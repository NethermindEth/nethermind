// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient;

internal sealed record CheckpointSelection(Hash256 Root, ulong Epoch, string Provider, DateTimeOffset SelectedAt, bool Reused);

internal static class CheckpointSource
{
    private const string FinalityPath = "eth/v1/beacon/states/finalized/finality_checkpoints";
    private const int MaxResponseBytes = 4096;
    private sealed record SavedCheckpoint(int Version, string Network, string Provider, string Root, ulong Epoch, DateTimeOffset SelectedAt);

    internal static string? DefaultUrl(string network) => network switch
    {
        "hoodi" => "https://checkpoint-sync.hoodi.ethpandaops.io",
        "sepolia" => "https://checkpoint-sync.sepolia.ethpandaops.io",
        _ => null,
    };

    /// <summary>Reuses the original provider-selected anchor, or records the first explicit provider choice.</summary>
    /// <remarks>The saved provenance is local configuration, not proof of canonicality. Journal replay still authenticates every update.</remarks>
    internal static async Task<CheckpointSelection> ResolveAsync(HttpClient client, string endpoint, string network,
        string directory, BeaconChainSpec spec, ulong currentSlot, string? crossCheckEndpoint, CancellationToken cancellationToken)
    {
        if (network is not ("mainnet" or "hoodi" or "sepolia")) throw new ArgumentException("Unsupported checkpoint network.", nameof(network));
        Uri provider = Endpoint(endpoint);
        ulong currentTime = checked(spec.GenesisTime + currentSlot * spec.SecondsPerSlot);
        string path = Path.Combine(directory, network + "-checkpoint.json");
        CheckpointSelection selected;
        if (File.Exists(path))
        {
            try
            {
                await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                byte[] bytes = new byte[MaxResponseBytes + 1];
                int length = await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
                if (length > MaxResponseBytes) throw new InvalidDataException("Saved checkpoint provenance exceeds its size limit.");
                SavedCheckpoint saved = JsonSerializer.Deserialize<SavedCheckpoint>(bytes.AsSpan(0, length))
                    ?? throw new InvalidDataException("Saved checkpoint provenance is empty.");
                if (saved.Version != 1 || saved.Network != network || saved.Provider != provider.AbsoluteUri)
                    throw new InvalidDataException("Saved checkpoint provenance belongs to a different network or provider. Select an explicit --checkpoint or a separate --data-dir to change the trust anchor.");
                selected = new(ParseRoot(saved.Root), saved.Epoch, saved.Provider, saved.SelectedAt, true);
                if (saved.Epoch > spec.GetEpoch(currentSlot)) throw new InvalidDataException("Saved checkpoint epoch is in the future.");
                long selectedTime = saved.SelectedAt.ToUnixTimeSeconds();
                if (selectedTime < 0 || (ulong)selectedTime < spec.GenesisTime || (ulong)selectedTime > currentTime)
                    throw new InvalidDataException("Saved checkpoint selection time is invalid or in the future.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Saved checkpoint provenance is invalid; it cannot be replaced implicitly.", exception);
            }
        }
        else
        {
            if (Directory.Exists(directory))
            {
                foreach (string journal in Directory.EnumerateFiles(directory, network + "-*.chain"))
                    throw new InvalidDataException($"Existing consensus journal {journal} has no saved checkpoint provenance. Restart with its original --checkpoint or a separate --data-dir instead of replacing the anchor implicitly.");
            }
            await RequireNetworkAsync(client, provider, spec, cancellationToken);
            using JsonDocument document = await RequestAsync(client, new Uri(provider, FinalityPath), cancellationToken);
            JsonElement response = document.RootElement;
            if (!OptionalFlag(response, "finalized", true) || !OptionalFlag(response, "execution_optimistic", false) ||
                !response.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("finalized", out JsonElement finalized) || finalized.ValueKind != JsonValueKind.Object ||
                !finalized.TryGetProperty("root", out JsonElement rootValue) || rootValue.ValueKind != JsonValueKind.String ||
                !finalized.TryGetProperty("epoch", out JsonElement epochValue) || !TryNumber(epochValue, out ulong epoch))
                throw new InvalidDataException("Checkpoint provider returned an invalid finalized response.");
            ulong currentEpoch = spec.GetEpoch(currentSlot);
            if (epoch > currentEpoch || currentEpoch - epoch > Presets.MinValidatorWithdrawabilityDelay)
                throw new InvalidDataException("Checkpoint provider epoch is in the future or older than the conservative 256-epoch weak-subjectivity bound. The full network weak-subjectivity period is not computed.");
            DateTimeOffset selectedAt = DateTimeOffset.FromUnixTimeSeconds(checked((long)currentTime));
            selected = new(ParseRoot(rootValue.GetString()), epoch, provider.AbsoluteUri, selectedAt, false);
        }
        if (crossCheckEndpoint is not null)
        {
            Uri crossCheck = Endpoint(crossCheckEndpoint);
            if (crossCheck.Host.Equals(provider.Host, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Checkpoint cross-check must use a different HTTPS provider host.", nameof(crossCheckEndpoint));
            await CrossCheckAsync(client, crossCheck.AbsoluteUri, selected.Root, spec, cancellationToken);
        }
        if (!selected.Reused)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedCheckpoint(1, network, selected.Provider,
                selected.Root.ToString(), selected.Epoch, selected.SelectedAt));
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    await output.WriteAsync(bytes, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return selected;
    }

    /// <summary>Requires a chosen HTTPS provider to report the supplied checkpoint as canonical and finalized on the selected network.</summary>
    /// <remarks>This is an independent provider assertion, not a P2P proof of canonicality.</remarks>
    internal static async Task CrossCheckAsync(HttpClient client, string endpoint, Hash256 checkpoint,
        BeaconChainSpec spec, CancellationToken cancellationToken)
    {
        Uri provider = Endpoint(endpoint);
        await RequireNetworkAsync(client, provider, spec, cancellationToken);
        using JsonDocument document = await RequestAsync(client, new Uri(provider, $"eth/v1/beacon/headers/{checkpoint}"), cancellationToken);
        JsonElement response = document.RootElement;
        if (!Flag(response, "finalized", true) || !Flag(response, "execution_optimistic", false) ||
            !response.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !Flag(data, "canonical", true) || !data.TryGetProperty("root", out JsonElement rootValue) ||
            rootValue.ValueKind != JsonValueKind.String || ParseRoot(rootValue.GetString()) != checkpoint)
            throw new InvalidDataException("Checkpoint cross-check provider does not confirm the selected root as canonical and finalized.");
    }

    private static Uri Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + '/', UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Checkpoint URL must be an HTTPS base URL without credentials, query or fragment.", nameof(endpoint));
        return uri;
    }

    private static async Task RequireNetworkAsync(HttpClient client, Uri provider, BeaconChainSpec spec, CancellationToken cancellationToken)
    {
        using JsonDocument document = await RequestAsync(client, new Uri(provider, "eth/v1/beacon/genesis"), cancellationToken);
        JsonElement response = document.RootElement;
        if (!response.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("genesis_validators_root", out JsonElement rootValue) || rootValue.ValueKind != JsonValueKind.String ||
            ParseRoot(rootValue.GetString()) != spec.GenesisValidatorsRoot ||
            !data.TryGetProperty("genesis_time", out JsonElement timeValue) || !TryNumber(timeValue, out ulong time) || time != spec.GenesisTime)
            throw new InvalidDataException("Checkpoint provider belongs to a different beacon network.");
    }

    private static async Task<JsonDocument> RequestAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
        try
        {
            JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new InvalidDataException("Checkpoint provider returned an invalid response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Checkpoint provider returned invalid JSON.", exception);
        }
    }

    private static bool Flag(JsonElement value, string property, bool expected) =>
        value.TryGetProperty(property, out JsonElement flag) && flag.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static bool OptionalFlag(JsonElement value, string property, bool expected) =>
        !value.TryGetProperty(property, out JsonElement flag) || flag.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static bool TryNumber(JsonElement value, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static Hash256 ParseRoot(string? root)
    {
        if (root is null || root.Length != 2 + Hash256.Size * 2 || !root.StartsWith("0x", StringComparison.Ordinal))
            throw new InvalidDataException("Checkpoint provider returned an invalid finalized block root.");
        byte[] bytes;
        try { bytes = Convert.FromHexString(root[2..]); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Checkpoint provider returned an invalid finalized block root.", exception);
        }
        Hash256 checkpoint = new(bytes);
        if (checkpoint == Hash256.Zero) throw new InvalidDataException("Checkpoint provider returned a zero finalized block root.");
        return checkpoint;
    }
}
