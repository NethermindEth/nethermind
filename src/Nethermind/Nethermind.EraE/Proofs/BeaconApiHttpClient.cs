// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.EraE.Proofs;

internal sealed class BeaconApiHttpClient(HttpClient? httpClient, TimeSpan requestTimeout, ILogger logger = default)
    : IDisposable
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly bool _ownsHttpClient = httpClient is null;

    public async Task<JsonDocument?> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(requestTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException($"Beacon API returned {(int)response.StatusCode} for {uri}.");

            // 404 is expected for missed slots; other 4xx (401, 403, 429) indicate misconfiguration or quota issues.
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                logger.Warn($"Beacon API returned {(int)response.StatusCode} for {uri}. Beacon roots will not be included for this slot.");

            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
