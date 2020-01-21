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
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.EvmPlayground
{
    internal class BasicJsonRpcClient : IJsonRpcClient
    {
        private readonly HttpClient _client;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public BasicJsonRpcClient(Uri uri)
            : this(uri, new EthereumJsonSerializer(), NullLogger.Instance)
        {
        }

        internal BasicJsonRpcClient(Uri uri, ILogger logger)
            : this(uri, new EthereumJsonSerializer(), logger)
        {
            _logger.Info($"Starting RPC client for {uri}");
        }

        private BasicJsonRpcClient(Uri uri, IJsonSerializer jsonSerializer, ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
            _jsonSerializer = jsonSerializer;

            _client = new HttpClient {BaseAddress = uri};
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<string> Post(string method, params object[] parameters)
        {
            try
            {
                string request = GetJsonRequest(method, parameters);
                HttpResponseMessage response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
                string content = await response.Content.ReadAsStringAsync();
                return content;
            }
            catch (Exception e)
            {
                _logger.Error($"Error during execution of {method}", e);
                return $"Error: {e.Message}";
            }
        }

        private string GetJsonRequest(string method, IEnumerable<object> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = 67,
                method,
                @params = parameters ?? Enumerable.Empty<object>()
            };

            return _jsonSerializer.Serialize(request);
        }
    }
}