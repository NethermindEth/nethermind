// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.EraE.Proofs;

public sealed class BeaconApiRootsProvider(
    Uri baseUrl,
    HttpClient? httpClient = null,
    TimeSpan? requestTimeout = null,
    int maxAttempts = 3,
    ILogManager? logManager = null)
    : IBeaconRootsProvider
{
    private readonly BeaconApiHttpClient _client = new(httpClient, requestTimeout ?? TimeSpan.FromSeconds(30), logManager?.GetClassLogger<BeaconApiHttpClient>() ?? default);
    private readonly BeaconApiRetry<(ValueHash256, ValueHash256)?> _beaconApiRetry = new(maxAttempts);

    public void Dispose() => _client.Dispose();

    public Task<(ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)?> GetBeaconRoots(
        long slot, CancellationToken cancellationToken = default) =>
        FetchWithRetryAsync(slot, cancellationToken);

    private Task<(ValueHash256, ValueHash256)?> FetchWithRetryAsync(
        long slot, CancellationToken cancellationToken) =>
        _beaconApiRetry.ExecuteAsync(async ct =>
        {
            ValueHash256? blockRoot = await FetchBlockRootAsync(slot, ct).ConfigureAwait(false);
            if (!blockRoot.HasValue)
                return null;

            ValueHash256? stateRoot = await FetchStateRootAsync(slot, ct).ConfigureAwait(false);
            if (!stateRoot.HasValue)
                return null;

            return (blockRoot.Value, stateRoot.Value);
        }, cancellationToken);

    private async Task<ValueHash256?> FetchBlockRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(baseUrl, $"/eth/v1/beacon/headers/{slot}");
        using JsonDocument? document = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        return document is null ? null : ExtractHash(document.RootElement, ["data", "root"]);
    }

    private async Task<ValueHash256?> FetchStateRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(baseUrl, $"/eth/v1/beacon/states/{slot}/root");
        using JsonDocument? document = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        return document is null ? null : ExtractHash(document.RootElement, ["data", "root"]);
    }

    private static ValueHash256? ExtractHash(JsonElement root, string[] path)
    {
        JsonElement element = root;
        foreach (string key in path)
        {
            if (!element.TryGetProperty(key, out JsonElement next))
                return null;
            element = next;
        }

        if (element.ValueKind != JsonValueKind.String)
            return null;

        string? hex = element.GetString();
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        try { return new ValueHash256(hex); }
        catch (FormatException) { return null; }
    }
}
