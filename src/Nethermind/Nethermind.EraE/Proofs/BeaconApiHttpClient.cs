// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text.Json;

namespace Nethermind.EraE.Proofs;

internal sealed class BeaconApiHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public BeaconApiHttpClient(HttpClient? httpClient, TimeSpan requestTimeout)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _requestTimeout = requestTimeout;
    }

    public async Task<JsonDocument?> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new HttpRequestException($"Beacon API returned {(int)response.StatusCode} for {uri}.");
            }
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
