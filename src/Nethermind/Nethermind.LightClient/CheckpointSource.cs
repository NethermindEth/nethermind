// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.LightClient;

internal static class CheckpointSource
{
    private const string FinalityPath = "eth/v1/beacon/states/finalized/finality_checkpoints";

    internal static string? DefaultUrl(string network) => network switch
    {
        "hoodi" => "https://checkpoint-sync.hoodi.ethpandaops.io",
        "sepolia" => "https://checkpoint-sync.sepolia.ethpandaops.io",
        _ => null,
    };

    internal static async Task<Hash256> FetchAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + '/', UriKind.Absolute, out Uri? baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.UserInfo.Length != 0 ||
            baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0)
            throw new ArgumentException("Checkpoint URL must be an HTTPS base URL without credentials, query or fragment.", nameof(endpoint));

        using HttpResponseMessage response = await client.GetAsync(new Uri(baseUri, FinalityPath),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4096, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("finalized", out JsonElement finalized) || finalized.ValueKind != JsonValueKind.Object ||
            !finalized.TryGetProperty("root", out JsonElement rootValue) || rootValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Checkpoint provider returned an invalid finality response.");

        string? root = rootValue.GetString();
        if (root is null || root.Length != 2 + Hash256.Size * 2 || !root.StartsWith("0x", StringComparison.Ordinal))
            throw new InvalidDataException("Checkpoint provider returned an invalid finalized block root.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(root[2..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Checkpoint provider returned an invalid finalized block root.", exception);
        }
        Hash256 checkpoint = new(bytes);
        if (checkpoint == Hash256.Zero) throw new InvalidDataException("Checkpoint provider returned a zero finalized block root.");
        return checkpoint;
    }
}
