//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
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

        public DefaultHttpClient(HttpClient client, IJsonSerializer jsonSerializer, ILogManager logManager,
            int retries = 3, int retryDelayMilliseconds = 1000)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _retries = retries;
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        public Task<T> GetAsync<T>(string endpoint)
            => ExecuteAsync<T>(Method.Get, endpoint);

        public  Task<T> PostJsonAsync<T>(string endpoint, object payload = null)
            => ExecuteAsync<T>(Method.Post, endpoint, payload);

        private async Task<T> ExecuteAsync<T>(Method method, string endpoint, object payload = null)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var methodType = method.ToString();
            var currentRetry = 0;
            do
            {
                try
                {
                    if (currentRetry > 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Retrying ({currentRetry}/{_retries}) sending HTTP {methodType} request to: {endpoint} [id: {requestId}].");
                    }
                    
                    currentRetry++;

                    return await ProcessRequestAsync<T>(method, endpoint, requestId, payload);
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

        private async Task<T> ProcessRequestAsync<T>(Method method, string endpoint, string requestId,
            object payload = null)
        {
            var methodType = method.ToString();
            var json = payload is null ? "{}" : _jsonSerializer.Serialize(payload);
            if (_logger.IsTrace) _logger.Trace($"Sending HTTP {methodType} request to: {endpoint} [id: {requestId}]{(method == Method.Get ? "." : $": {json}")}");
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            HttpResponseMessage response;
            switch (method)
            {
                case Method.Get: response = await _client.GetAsync(endpoint);
                    break;
                case Method.Post: 
                    var payloadContent = new StringContent(json, Encoding.UTF8, "application/json");
                    response = await _client.PostAsync(endpoint, payloadContent);
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
            
            var content = await response.Content.ReadAsStringAsync();
            
            return _jsonSerializer.Deserialize<T>(content);
        }

        private enum Method
        {
            Get,
            Post
        }
    }
}
