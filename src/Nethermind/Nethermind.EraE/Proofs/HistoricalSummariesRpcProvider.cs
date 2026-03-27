// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.EraE.Proofs;

public class HistoricalSummariesRpcProvider : IHistoricalSummariesProvider
{
    private readonly BeaconApiHttpClient _client;
    private readonly Uri _baseUrl;
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
        _client = new BeaconApiHttpClient(httpClient, requestTimeout ?? TimeSpan.FromSeconds(30));
        _maxRetries = maxRetries;
    }

    public void Dispose() => _client.Dispose();

    public async Task<HistoricalSummary?> GetHistoricalSummary(
        int index,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        HistoricalSummary[] summaries = await GetHistoricalSummariesAsync("head", cancellationToken, forceRefresh).ConfigureAwait(false);
        return summaries.Length > index ? summaries[index] : null;
    }

    public async Task<HistoricalSummary[]> GetHistoricalSummariesAsync(
        string stateId = "head",
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (_cachedSummaries.Length > 0 && !forceRefresh)
            return _cachedSummaries;

        _cachedSummaries = await LoadWithRetryAsync(stateId, cancellationToken).ConfigureAwait(false);
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
        using JsonDocument? document = await _client.GetAsync(fullUri, cancellationToken).ConfigureAwait(false);
        return document is null ? [] : ParseHistoricalSummaries(document.RootElement);
    }

    private static HistoricalSummary[] ParseHistoricalSummaries(JsonElement root)
    {
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
