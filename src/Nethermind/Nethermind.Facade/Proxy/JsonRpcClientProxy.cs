//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Facade.Proxy
{
    public class JsonRpcClientProxy : IJsonRpcClientProxy
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly HttpClient _client;
        private readonly ILogger _logger;
        private readonly int _retries;
        private readonly int _retryDelayMilliseconds;

        public JsonRpcClientProxy(string[] urlProxies, IJsonSerializer jsonSerializer, ILogManager logManager,
            int retries = 3, int retryDelayMilliseconds = 1000)
        {
            var url = urlProxies?.FirstOrDefault() ??
                      throw new ArgumentException("Empty JSON RPC URL proxies.", nameof(urlProxies));
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("Empty JSON RPC URL proxy.", nameof(url));
            }

            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _retries = retries;
            _retryDelayMilliseconds = retryDelayMilliseconds;
            _client = new HttpClient
            {
                BaseAddress = new Uri(url)
            };
        }

        public Task<RpcResult<T>> SendAsync<T>(string method, params object[] @params)
            => SendAsync<T>(method, 1, @params);

        public Task<RpcResult<T>> SendAsync<T>(string method, long id, params object[] @params)
        {
            var request = new
            {
                jsonrpc = 2.0,
                id,
                method,
                @params = (@params ?? Array.Empty<object>()).Where(x => !(x is null))
            };

            var requestId = Guid.NewGuid().ToString();
            var json = _jsonSerializer.Serialize(request);
            var payload = new StringContent(json, Encoding.UTF8, "application/json");
            if (_logger.IsTrace) _logger.Trace($"Sending JSON RPC Proxy request [id: {requestId}]: {json}");

            return TrySendAsync<T>(requestId, payload);
        }

        private async Task<RpcResult<T>> TrySendAsync<T>(string requestId, HttpContent payload)
        {
            var currentRetry = 0;
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            do
            {
                try
                {
                    if (currentRetry > 0)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Retrying ({currentRetry}/{_retries}) sending JSON RPC Proxy request [id: {requestId}].");
                    }
                    
                    currentRetry++;
                    var result = await _client.PostAsync(string.Empty, payload);
                    stopWatch.Stop();
                    var response = await result.Content.ReadAsStringAsync();
                    if (_logger.IsTrace) _logger.Trace($"Received JSON RPC Proxy response [id: {requestId}, elapsed: {stopWatch.ElapsedMilliseconds} ms]: {response}");

                    return _jsonSerializer.Deserialize<RpcResult<T>>(response);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error(ex.Message, ex);
                    if (currentRetry == _retries)
                    {
                        break;
                    }
                    
                    if (_logger.IsTrace) _logger.Trace($"JSON RPC Proxy request [id: {requestId}] will be sent again in: {_retryDelayMilliseconds} ms.");
                    await Task.Delay(_retryDelayMilliseconds);
                    stopWatch.Restart();
                }
            } while (currentRetry <= _retries);
            
            return RpcResult<T>.Invalid($"There was an error when sending JSON RPC Proxy request [id: {requestId}].");
        }
    }
}