// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Prometheus;

namespace Nethermind.Monitoring;

using System.Diagnostics;
using System.Text;

/// <summary>
/// A metric server that regularly pushes metrics to a Prometheus PushGateway.
/// </summary>
public class MetricPusher : MetricHandler
{
    private readonly TimeSpan _pushInterval;
    private readonly HttpMethod _method;
    private readonly Uri _targetUrl;
    private readonly Func<HttpClient> _httpClientProvider;
    private readonly SemaphoreSlim _wait;

    public MetricPusher(MetricPusherOptions options)
    {
        if (string.IsNullOrEmpty(options.Endpoint))
            throw new ArgumentNullException(nameof(options.Endpoint));

        if (string.IsNullOrEmpty(options.Job))
            throw new ArgumentNullException(nameof(options.Job));

        if (options.IntervalMilliseconds <= 0)
            throw new ArgumentException("Interval must be greater than zero", nameof(options.IntervalMilliseconds));

        _registry = options.Registry ?? Prometheus.Metrics.DefaultRegistry;

        _httpClientProvider = options.HttpClientProvider ?? (() => SingletonHttpClient);

        StringBuilder sb = new StringBuilder($"{options.Endpoint!.TrimEnd('/')}/job/{options.Job}");
        if (!string.IsNullOrEmpty(options.Instance))
            sb.Append($"/instance/{options.Instance}");

        if (options.AdditionalLabels != null)
        {
            foreach (Tuple<string, string> pair in options.AdditionalLabels)
            {
                if (pair == null || string.IsNullOrEmpty(pair.Item1) || string.IsNullOrEmpty(pair.Item2))
                    throw new NotSupportedException(
                        $"Invalid {nameof(MetricPusher)} additional label: ({pair?.Item1}):({pair?.Item2})");

                sb.Append($"/{pair.Item1}/{pair.Item2}");
            }
        }

        if (!Uri.TryCreate(sb.ToString(), UriKind.Absolute, out Uri? targetUrl))
        {
            throw new ArgumentException("Endpoint must be a valid url", nameof(options.Endpoint));
        }

        _targetUrl = targetUrl;

        _pushInterval = TimeSpan.FromMilliseconds(options.IntervalMilliseconds);
        _onError = options.OnError;

        _method = options.ReplaceOnPush ? HttpMethod.Put : HttpMethod.Post;
        _wait = new SemaphoreSlim(0);
    }

    private static readonly HttpClient SingletonHttpClient = new();
    private readonly CollectorRegistry _registry;
    private readonly Action<Exception>? _onError;

    protected override Task StartServer(CancellationToken cancel)
    {
        return Task.Run(async delegate
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await _wait.WaitAsync(_pushInterval, cancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await PushOnce();
            }

            // Push the final state
            await PushOnce();
        });
    }

    public void Push() => _wait.Release();

    private async Task PushOnce()
    {
        try
        {
            HttpClient httpClient = _httpClientProvider();

            var request = new HttpRequestMessage
            {
                Method = _method,
                RequestUri = _targetUrl,
                // We use a copy-pasted implementation of PushStreamContent here to avoid taking a dependency on the old ASP.NET Web API where it lives.
                Content =
                    new PushRegistryHttpContent(_registry, PrometheusConstants.ExporterContentTypeValue),
            };

            // ReSharper disable once MethodSupportsCancellation
            using HttpResponseMessage response = await httpClient.SendAsync(request);

            // If anything goes wrong, we want to get at least an entry in the trace log.
            response.EnsureSuccessStatusCode();
        }
        catch (ScrapeFailedException ex)
        {
            // We do not consider failed scrapes a reportable error since the user code that raises the failure should be the one logging it.
            Trace.WriteLine($"Skipping metrics push due to failed scrape: {ex.Message}");
        }
        catch (Exception ex)
        {
            HandleFailedPush(ex);
        }
    }

    private void HandleFailedPush(Exception ex)
    {
        if (_onError != null)
        {
            // Asynchronous because we don't trust the callee to be fast.
            Task.Run(() => _onError(ex));
        }
        else
        {
            // If there is no error handler registered, we write to trace to at least hopefully get some attention to the problem.
            Trace.WriteLine(string.Format("Error in MetricPusher: {0}", ex));
        }
    }

    private sealed class PushRegistryHttpContent : HttpContent
    {
        private readonly CollectorRegistry _registry;

        private static readonly MediaTypeHeaderValue OctetStreamHeaderValue =
            MediaTypeHeaderValue.Parse("application/octet-stream");

        /// <summary>
        /// Initializes a new instance of the <see cref="Prometheus.PushStreamContentInternal"/> class with the given <see cref="MediaTypeHeaderValue"/>.
        /// </summary>
        public PushRegistryHttpContent(CollectorRegistry registry, MediaTypeHeaderValue? mediaType)
        {
            _registry = registry;
            Headers.ContentType = mediaType ?? OctetStreamHeaderValue;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await _registry.CollectAndExportAsTextAsync(stream, default);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = default;
            return false;
        }
    }
}
