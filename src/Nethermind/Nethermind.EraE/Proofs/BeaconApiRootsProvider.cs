// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

public sealed class BeaconApiRootsProvider : IBeaconRootsProvider
{
    private readonly BeaconApiHttpClient _client;
    private readonly Uri _baseUrl;
    private readonly int _maxRetries;
    private readonly ConcurrentDictionary<long, (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)> _cache = new();

    public BeaconApiRootsProvider(
        Uri baseUrl,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        int maxRetries = 3)
    {
        _baseUrl = baseUrl;
        _client = new BeaconApiHttpClient(httpClient, requestTimeout ?? TimeSpan.FromSeconds(30));
        _maxRetries = maxRetries;
    }

    public void Dispose() => _client.Dispose();

    public async Task<(ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)?> GetBeaconRoots(
        long slot, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(slot, out (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot) cached))
            return cached;

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result =
            await FetchWithRetryAsync(slot, cancellationToken).ConfigureAwait(false);

        if (result.HasValue)
            _cache[slot] = result.Value;

        return result;
    }

    private Task<(ValueHash256, ValueHash256)?> FetchWithRetryAsync(
        long slot, CancellationToken cancellationToken) =>
        BeaconApiRetry.RetryAsync(async ct =>
        {
            ValueHash256? blockRoot = await FetchBlockRootAsync(slot, ct).ConfigureAwait(false);
            if (!blockRoot.HasValue)
                return default((ValueHash256, ValueHash256)?);

            ValueHash256? stateRoot = await FetchStateRootAsync(slot, ct).ConfigureAwait(false);
            if (!stateRoot.HasValue)
                return default;

            return (blockRoot.Value, stateRoot.Value);
        }, _maxRetries, cancellationToken);

    private async Task<ValueHash256?> FetchBlockRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(_baseUrl, $"/eth/v1/beacon/headers/{slot}");
        using JsonDocument? document = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        return document is null ? null : ExtractHash(document.RootElement, ["data", "root"]);
    }

    private async Task<ValueHash256?> FetchStateRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(_baseUrl, $"/eth/v1/beacon/states/{slot}/root");
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
