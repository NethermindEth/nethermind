// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text.Json;

namespace Nethermind.EraE.Proofs;

/// <summary>
/// Fetches historical summaries from the Beacon Chain API by streaming
/// /eth/v2/debug/beacon/states/{state_id} and extracting the historical_summaries array.
/// Results are cached; use <c>forceRefresh</c> to bypass the cache.
/// </summary>
public class HistoricalSummariesRpcProvider : IHistoricalSummariesProvider
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUrl;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxRetries;
    private const string Endpoint = "/eth/v2/debug/beacon/states";

    private HistoricalSummary[] _cachedSummaries = [];

    public HistoricalSummariesRpcProvider(
        Uri baseUrl,
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        int maxRetries = 3)
    {
        _baseUrl = baseUrl;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _maxRetries = maxRetries;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public async Task<HistoricalSummary?> GetHistoricalSummary(
        int index,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        HistoricalSummary[] summaries = await GetHistoricalSummariesAsync("head", cancellationToken, forceRefresh);
        return summaries.Length > index ? summaries[index] : null;
    }

    public async Task<HistoricalSummary[]> GetHistoricalSummariesAsync(
        string stateId = "head",
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (_cachedSummaries.Length > 0 && !forceRefresh)
            return _cachedSummaries;

        _cachedSummaries = await LoadWithRetryAsync(stateId, cancellationToken);
        return _cachedSummaries;
    }

    private Task<HistoricalSummary[]> LoadWithRetryAsync(
        string stateId,
        CancellationToken cancellationToken) =>
        BeaconApiRetry.RetryAsync(
            ct => LoadHistoricalSummariesAsync(stateId, ct),
            _maxRetries,
            cancellationToken);

    private async Task<HistoricalSummary[]> LoadHistoricalSummariesAsync(
        string stateId,
        CancellationToken cancellationToken)
    {
        Uri fullUri = new(_baseUrl, $"{Endpoint}/{stateId}");

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, fullUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        return await ParseHistoricalSummariesFromStreamAsync(stream, timeoutCts.Token);
    }

    private static async Task<HistoricalSummary[]> ParseHistoricalSummariesFromStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("data", out JsonElement data))
            return [];

        if (!data.TryGetProperty("historical_summaries", out JsonElement historicalSummaries))
            return [];

        if (historicalSummaries.ValueKind != JsonValueKind.Array)
            return [];

        List<HistoricalSummary> summaries = new(historicalSummaries.GetArrayLength());
        foreach (JsonElement summary in historicalSummaries.EnumerateArray())
        {
            if (!summary.TryGetProperty("block_summary_root", out JsonElement blockSummaryRootEl))
                continue;
            if (!summary.TryGetProperty("state_summary_root", out JsonElement stateSummaryRootEl))
                continue;

            string? blockSummaryRoot = blockSummaryRootEl.GetString();
            string? stateSummaryRoot = stateSummaryRootEl.GetString();
            if (blockSummaryRoot is null || stateSummaryRoot is null)
                continue;

            summaries.Add(HistoricalSummary.From(blockSummaryRoot, stateSummaryRoot));
        }

        return summaries.ToArray();
    }
}
