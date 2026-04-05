// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.EraE.Proofs;

public class HistoricalSummariesRpcProvider(
    Uri baseUrl,
    HttpClient? httpClient = null,
    TimeSpan? requestTimeout = null,
    int maxRetries = 3) : IHistoricalSummariesProvider
{
    private readonly BeaconApiHttpClient _client = new(httpClient, requestTimeout ?? TimeSpan.FromSeconds(30));
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string Endpoint = "/eth/v2/debug/beacon/states";

    private HistoricalSummary[] _cachedSummaries = [];

    public void Dispose()
    {
        _client.Dispose();
        _lock.Dispose();
    }

    public async Task<HistoricalSummary?> GetHistoricalSummary(
        int index,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        HistoricalSummary[] summaries = await GetHistoricalSummariesAsync("head", forceRefresh, cancellationToken).ConfigureAwait(false);
        return summaries.Length > index ? summaries[index] : null;
    }

    public async Task<HistoricalSummary[]> GetHistoricalSummariesAsync(
        string stateId = "head",
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (_cachedSummaries.Length > 0 && !forceRefresh)
            return _cachedSummaries;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSummaries.Length > 0 && !forceRefresh)
                return _cachedSummaries;

            _cachedSummaries = await LoadWithRetryAsync(stateId, cancellationToken).ConfigureAwait(false);
            return _cachedSummaries;
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task<HistoricalSummary[]> LoadWithRetryAsync(
        string stateId,
        CancellationToken cancellationToken) =>
        BeaconApiRetry.RetryAsync(
            ct => LoadHistoricalSummariesAsync(stateId, ct),
            maxRetries,
            cancellationToken);

    private async Task<HistoricalSummary[]> LoadHistoricalSummariesAsync(
        string stateId,
        CancellationToken cancellationToken)
    {
        Uri fullUri = new(baseUrl, $"{Endpoint}/{stateId}");
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

        return [.. summaries];
    }
}
