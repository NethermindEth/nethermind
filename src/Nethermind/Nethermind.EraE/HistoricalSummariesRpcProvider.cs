// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.EraE;

/// <summary>
/// Represents a historical summary of a beacon state.
/// </summary>
public struct HistoricalSummary
{
    public ValueHash256 BlockSummaryRoot { get; set; }
    public ValueHash256 StateSummaryRoot { get; set; }


    public HistoricalSummary(ValueHash256 blockSummaryRoot, ValueHash256 stateSummaryRoot) {
        BlockSummaryRoot = blockSummaryRoot;
        StateSummaryRoot = stateSummaryRoot;
    }

    public static HistoricalSummary From(string blockSummaryRootHex, string stateSummaryRootHex) {
        return new HistoricalSummary(new ValueHash256(blockSummaryRootHex), new ValueHash256(stateSummaryRootHex));
    }
}


public interface IHistoricalSummariesProvider: IDisposable
{
    Task<HistoricalSummary?> GetHistoricalSummary(int index, CancellationToken cancellationToken = default, bool forceRefresh = false);
    Task<HistoricalSummary[]> GetHistoricalSummariesAsync(string stateId = "head", CancellationToken cancellationToken = default, bool forceRefresh = false);
}

/// <summary>
/// Provides historical summaries from Beacon Chain API provider.
/// Queries the /eth/v2/debug/beacon/states/{state_id} endpoint and extracts historical_summaries in streaming mode.
/// </summary>
public class HistoricalSummariesRpcProvider : IHistoricalSummariesProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private const string Endpoint = "/eth/v2/debug/beacon/states";

    private HistoricalSummary[] _cachedSummaries = Array.Empty<HistoricalSummary>();

    public HistoricalSummariesRpcProvider(Uri baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl;
        _httpClient = httpClient ?? new HttpClient();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<HistoricalSummary?> GetHistoricalSummary(int index, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        HistoricalSummary[] summaries = await GetHistoricalSummariesAsync("head", cancellationToken, forceRefresh);
        return summaries.Length > index ? summaries[index] : null;
    }

    public async Task<HistoricalSummary[]> GetHistoricalSummariesAsync(
        string stateId = "head", 
        CancellationToken cancellationToken = default, 
        bool forceRefresh = false
    ) {
        // return cached summaries if available and not forced to refresh
        if (_cachedSummaries.Length > 0 && !forceRefresh) return _cachedSummaries;

        HistoricalSummary[] summaries = await LoadHistoricalSummariesAsync(stateId, cancellationToken);
        _cachedSummaries = summaries;
        return _cachedSummaries;
    }

    /// <summary>
    /// Fetches historical summaries from the beacon state.
    /// </summary>
    /// <param name="stateId">The state ID (e.g., "head", "finalized", or a slot number). Defaults to "head".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of historical summary roots.</returns>
    private async Task<HistoricalSummary[]> LoadHistoricalSummariesAsync(string stateId = "head", CancellationToken cancellationToken = default)
    {
        
        string requestUri = $"{Endpoint}/{stateId}";
        Uri fullUri = new(_baseUrl, requestUri);

        using HttpRequestMessage request = new(HttpMethod.Get, fullUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        
        return await ParseHistoricalSummariesFromStreamAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Parses historical summaries from the streaming JSON response.
    /// Historical summaries are an array of objects with a "block_summary_root" field (32 bytes as hex string).
    /// </summary>
    /// <param name="stream">The response stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of historical summary roots as hex strings.</returns>
    private static async Task<HistoricalSummary[]> ParseHistoricalSummariesFromStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        
        JsonElement root = document.RootElement;
        
        // Navigate to data.historical_summaries
        if (root.TryGetProperty("data", out JsonElement data))
        {
            if (data.TryGetProperty("historical_summaries", out JsonElement historicalSummaries))
            {
                if (historicalSummaries.ValueKind == JsonValueKind.Array)
                {
                    int arrayLength = historicalSummaries.GetArrayLength();
                    List<HistoricalSummary> summaries = new(arrayLength);
                    
                    foreach (JsonElement summary in historicalSummaries.EnumerateArray())
                    {
                        var historicalSummary = HistoricalSummary.From(
                            summary.GetProperty("block_summary_root").GetString(),
                            summary.GetProperty("state_summary_root").GetString()
                        );
                        summaries.Add(historicalSummary);
                    }
                    
                    return summaries.ToArray();
                }
            }
        }
        
        return Array.Empty<HistoricalSummary>();
    }

    /// <summary>
    /// Fetches the raw JSON response for debugging purposes.
    /// </summary>
    /// <param name="stateId">The state ID (e.g., "head").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw JSON string.</returns>
    public async Task<string> GetRawResponseAsync(string stateId = "head", CancellationToken cancellationToken = default)
    {
        string requestUri = $"{Endpoint}/{stateId}";
        Uri fullUri = new(_baseUrl, requestUri);

        using HttpRequestMessage request = new(HttpMethod.Get, fullUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}