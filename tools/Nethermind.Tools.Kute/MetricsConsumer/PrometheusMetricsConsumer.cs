// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Net.Http.Headers;
using App.Metrics.Formatters.Prometheus;

namespace Nethermind.Tools.Kute.MetricsConsumer;

public class PrometheusMetricsConsumer : IMetricsConsumer
{
    private readonly MetricsPrometheusTextOutputFormatter _formatter;
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    public PrometheusMetricsConsumer(
        HttpClient httpClient,
        MetricsPrometheusTextOutputFormatter formatter,
        string endpoint,
        string? basicAuthUsername = null,
        string? basicAuthPassword = null
    )
    {
        _formatter = formatter;
        _httpClient = httpClient;
        _endpoint = endpoint;
        if (!string.IsNullOrEmpty(basicAuthUsername) && !string.IsNullOrEmpty(basicAuthPassword))
        {
            var headerValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicAuthUsername}:{basicAuthPassword}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", headerValue);
        }
    }

    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        using (var stream = new MemoryStream())
        {
            await _formatter.WriteAsync(stream, snapshot);
            stream.Position = 0;
            HttpContent content = new StreamContent(stream);
            var response = await _httpClient.PostAsync(_endpoint, content);
            response.EnsureSuccessStatusCode();
        }
    }
}
