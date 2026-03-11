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
    private readonly Uri _baseUrl;
    private const string Endpoint = "/eth/v2/debug/beacon/states";

    private HistoricalSummary[] _cachedSummaries = [];

    public HistoricalSummariesRpcProvider(Uri baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl;
        _httpClient = httpClient ?? new HttpClient();
    }

    public void Dispose() => _httpClient.Dispose();

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

        _cachedSummaries = await LoadHistoricalSummariesAsync(stateId, cancellationToken);
        return _cachedSummaries;
    }

    private async Task<HistoricalSummary[]> LoadHistoricalSummariesAsync(
        string stateId,
        CancellationToken cancellationToken)
    {
        Uri fullUri = new(_baseUrl, $"{Endpoint}/{stateId}");

        using HttpRequestMessage request = new(HttpMethod.Get, fullUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ParseHistoricalSummariesFromStreamAsync(stream, cancellationToken);
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
            // Bug fix: use `summary` (the loop element), not `data` (the outer object)
            if (!summary.TryGetProperty("block_summary_root", out JsonElement blockSummaryRoot))
                continue;
            if (!summary.TryGetProperty("state_summary_root", out JsonElement stateSummaryRoot))
                continue;

            summaries.Add(HistoricalSummary.From(
                blockSummaryRoot.GetString()!,
                stateSummaryRoot.GetString()!));
        }

        return summaries.ToArray();
    }
}
