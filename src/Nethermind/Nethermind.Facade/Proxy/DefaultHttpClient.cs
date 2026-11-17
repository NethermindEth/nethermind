// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Proxy
{
    /// <summary>
    /// Sends JSON requests, and sends a request again when it fails before a response arrives.
    /// </summary>
    /// <param name="attempts">The most sends of one request, the first included; below 1 sends once.</param>
    /// <param name="retryDelayMilliseconds">The wait before sending again; below 0 does not wait.</param>
    public class DefaultHttpClient(
        HttpClient client,
        IJsonSerializer jsonSerializer,
        ILogManager logManager,
        int attempts = 3,
        int retryDelayMilliseconds = 100) : IHttpClient, IDisposable
    {
        private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));
        private readonly IJsonSerializer _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        private readonly ILogger _logger = logManager?.GetClassLogger<DefaultHttpClient>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly int _attempts = Math.Max(1, attempts);
        private readonly int _retryDelayMilliseconds = Math.Max(0, retryDelayMilliseconds);

        public Task<T?> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default)
            => ExecuteAsync<T>(Method.Get, endpoint, cancellationToken: cancellationToken);

        public Task<T?> PostJsonAsync<T>(string endpoint, object? payload = null, CancellationToken cancellationToken = default)
            => ExecuteAsync<T>(Method.Post, endpoint, payload, cancellationToken);

        private async Task<T?> ExecuteAsync<T>(Method method, string endpoint, object? payload = null, CancellationToken cancellationToken = default)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string methodType = method.ToString();
            string? content;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Retrying ({attempt}/{_attempts}) sending HTTP {methodType} request to: {SensitiveLogMasking.SafeUrl(endpoint)} [id: {requestId}].");
                    }

                    content = await ProcessRequestAsync(method, endpoint, requestId, payload, cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller cancellation has to surface as-is instead of being turned into a default result once the retries run out
                    throw;
                }
                catch (Exception ex)
                {
                    LogFailure(methodType, endpoint, ex);
                    if (attempt >= _attempts)
                    {
                        return default;
                    }

                    if (_logger.IsTrace) _logger.Trace($"HTTP {methodType} request to: {SensitiveLogMasking.SafeUrl(endpoint)} [id: {requestId}] will be sent again in: {_retryDelayMilliseconds} ms.");
                    await Task.Delay(_retryDelayMilliseconds, cancellationToken);
                }
            }

            if (content is null)
            {
                return default;
            }

            // A body that does not parse is no reason to send again: the same body would come back, and the request, a
            // submitted block for example, may already have taken effect.
            try
            {
                return _jsonSerializer.Deserialize<T>(content);
            }
            catch (Exception ex)
            {
                LogFailure(methodType, endpoint, ex);
                return default;
            }
        }

        private void LogFailure(string methodType, string endpoint, Exception ex)
        {
            if (_logger.IsError)
            {
                if (SensitiveLogMasking.Enabled)
                    _logger.Error($"HTTP {methodType} request to {SensitiveLogMasking.SafeUrl(endpoint)} failed: {ex.GetType().Name}");
                else
                    _logger.Error(ex.Message, ex);
            }
        }

        /// <returns>The body of a successful response, or <see langword="null"/> for any other status.</returns>
        private async Task<string?> ProcessRequestAsync(Method method, string endpoint, string requestId, object? payload = null, CancellationToken cancellationToken = default)
        {
            string methodType = method.ToString();
            string json = payload is null ? "{}" : _jsonSerializer.Serialize(payload);
            if (_logger.IsTrace) _logger.Trace($"Sending HTTP {methodType} request to: {SensitiveLogMasking.SafeUrl(endpoint)} [id: {requestId}]{(method == Method.Get ? "." : $": {json}")}");
            long startTime = Stopwatch.GetTimestamp();
            HttpResponseMessage? response = null;
            try
            {
                switch (method)
                {
                    case Method.Get:
                        response = await _client.GetAsync(endpoint, cancellationToken);
                        break;
                    case Method.Post:
                        response = await _client.PostAsync(endpoint, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
                        break;
                    default:
                        if (_logger.IsError) _logger.Error($"Unsupported HTTP method: {methodType}.");
                        return default;
                }
                if (_logger.IsTrace) _logger.Trace($"Received HTTP {methodType} response from: {SensitiveLogMasking.SafeUrl(endpoint)} [id: {requestId}, elapsed: {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0} ms]: {response}");
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            finally
            {
                response?.Dispose();
            }
        }

        private enum Method
        {
            Get,
            Post
        }

        public void Dispose() => _client?.Dispose();
    }
}
