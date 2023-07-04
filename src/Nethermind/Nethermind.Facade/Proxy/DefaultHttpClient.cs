// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
    public class DefaultHttpClient : IHttpClient
    {
        private readonly HttpClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly int _retries;
        private readonly int _retryDelayMilliseconds;

        public DefaultHttpClient(
            HttpClient client,
            IJsonSerializer jsonSerializer,
            ILogManager logManager,
            int retries = 3,
            int retryDelayMilliseconds = 1000)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _retries = retries;
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        public Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default)
            => ExecuteAsync<T>(Method.Get, endpoint, cancellationToken: cancellationToken);

        public Task<T> PostJsonAsync<T>(string endpoint, object? payload = null, CancellationToken cancellationToken = default)
            => ExecuteAsync<T>(Method.Post, endpoint, payload, cancellationToken);

        private async Task<T> ExecuteAsync<T>(Method method, string endpoint, object? payload = null, CancellationToken cancellationToken = default)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string methodType = method.ToString();
            int currentRetry = 0;
            do
            {
                try
                {
                    if (currentRetry > 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Retrying ({currentRetry}/{_retries}) sending HTTP {methodType} request to: {endpoint} [id: {requestId}].");
                    }

                    currentRetry++;

                    return await ProcessRequestAsync<T>(method, endpoint, requestId, payload, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error(ex.Message, ex);
                    if (currentRetry == _retries)
                    {
                        break;
                    }

                    if (_logger.IsTrace) _logger.Trace($"HTTP {methodType} request to: {endpoint} [id: {requestId}] will be sent again in: {_retryDelayMilliseconds} ms.");
                    await Task.Delay(_retryDelayMilliseconds);
                }
            } while (currentRetry <= _retries);

            return default;
        }

        private async Task<T> ProcessRequestAsync<T>(Method method, string endpoint, string requestId, object? payload = null, CancellationToken cancellationToken = default)
        {
            string methodType = method.ToString();
            string json = payload is null ? "{}" : _jsonSerializer.Serialize(payload);
            if (_logger.IsTrace) _logger.Trace($"Sending HTTP {methodType} request to: {endpoint} [id: {requestId}]{(method == Method.Get ? "." : $": {json}")}");
            Stopwatch stopWatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            switch (method)
            {
                case Method.Get:
                    response = await _client.GetAsync(endpoint, cancellationToken);
                    break;
                case Method.Post:
                    StringContent payloadContent = new(json, Encoding.UTF8, "application/json");
                    response = await _client.PostAsync(endpoint, payloadContent, cancellationToken);
                    break;
                default:
                    if (_logger.IsError) _logger.Error($"Unsupported HTTP method: {methodType}.");
                    return default;
            }

            stopWatch.Stop();
            if (_logger.IsTrace) _logger.Trace($"Received HTTP {methodType} response from: {endpoint} [id: {requestId}, elapsed: {stopWatch.ElapsedMilliseconds} ms]: {response}");
            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);

            return _jsonSerializer.Deserialize<T>(content);
        }

        private enum Method
        {
            Get,
            Post
        }
    }
}
