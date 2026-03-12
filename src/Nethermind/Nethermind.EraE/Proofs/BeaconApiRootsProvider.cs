// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

/// <summary>
/// Fetches beacon block roots and state roots from a Beacon Chain REST API.
/// Endpoints used:
///   GET /eth/v1/beacon/headers/{slot}       → beacon block root
///   GET /eth/v1/beacon/states/{slot}/root   → state root
/// Results are cached by slot to avoid duplicate requests.
/// </summary>
public sealed class BeaconApiRootsProvider : IBeaconRootsProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxRetries;
    private readonly Dictionary<long, (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)> _cache = new();

    public BeaconApiRootsProvider(
        Uri baseUrl,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        int maxRetries = 3)
    {
        _baseUrl = baseUrl;
        _httpClient = httpClient ?? new HttpClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _maxRetries = maxRetries;
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<(ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)?> GetBeaconRoots(
        long slot, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(slot, out (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot) cached))
            return cached;

        (ValueHash256 BeaconBlockRoot, ValueHash256 StateRoot)? result =
            await FetchWithRetryAsync(slot, cancellationToken);

        if (result.HasValue)
            _cache[slot] = result.Value;

        return result;
    }

    private async Task<(ValueHash256, ValueHash256)?> FetchWithRetryAsync(
        long slot, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                ValueHash256? blockRoot = await FetchBlockRootAsync(slot, cancellationToken);
                if (!blockRoot.HasValue)
                    return null;

                ValueHash256? stateRoot = await FetchStateRootAsync(slot, cancellationToken);
                if (!stateRoot.HasValue)
                    return null;

                return (blockRoot.Value, stateRoot.Value);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _maxRetries - 1)
            {
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    private async Task<ValueHash256?> FetchBlockRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(_baseUrl, $"/eth/v1/beacon/headers/{slot}");
        string? hex = await GetJsonStringFieldAsync(uri, ["data", "root"], cancellationToken);

        if (hex is null)
            return null;

        if (!TryParseHash256(hex, out ValueHash256 value))
            return null;

        return value;
    }

    private async Task<ValueHash256?> FetchStateRootAsync(long slot, CancellationToken cancellationToken)
    {
        Uri uri = new(_baseUrl, $"/eth/v1/beacon/states/{slot}/root");
        string? hex = await GetJsonStringFieldAsync(uri, ["data", "root"], cancellationToken);

        if (hex is null)
            return null;

        if (!TryParseHash256(hex, out ValueHash256 value))
            return null;

        return value;
    }

    private async Task<string?> GetJsonStringFieldAsync(
        Uri uri, string[] path, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (!response.IsSuccessStatusCode)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        JsonElement element = document.RootElement;
        foreach (string key in path)
        {
            if (!element.TryGetProperty(key, out JsonElement next))
                return null;

            element = next;
        }

        if (element.ValueKind != JsonValueKind.String)
            return null;

        string? value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryParseHash256(string? hex, out ValueHash256 value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(hex))
            return false;

        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (hex.Length != 64)
            return false;

        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            value = new ValueHash256(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}
