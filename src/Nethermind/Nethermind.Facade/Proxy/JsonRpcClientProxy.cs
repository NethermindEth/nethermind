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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Facade.Proxy
{
    public class JsonRpcClientProxy : IJsonRpcClientProxy
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly HttpClient _client;

        public JsonRpcClientProxy(string[] urlProxies, IJsonSerializer jsonSerializer)
        {
            var url = urlProxies?.FirstOrDefault() ??
                      throw new ArgumentException("Empty JSON RPC URL proxies.", nameof(urlProxies));
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("Empty JSON RPC URL proxy.", nameof(url));
            }

            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _client = new HttpClient
            {
                BaseAddress = new Uri(url)
            };
        }

        public Task<RpcResult<T>> SendAsync<T>(string method, params object[] @params)
            => SendAsync<T>(method, 1, @params);

        public async Task<RpcResult<T>> SendAsync<T>(string method, long id, params object[] @params)
        {
            var request = new
            {
                jsonrpc = 2.0,
                id,
                method,
                @params = (@params ?? Array.Empty<object>()).Where(x => !(x is null))
            };

            var payload = GetPayload(request);
            var result = await _client.PostAsync(string.Empty, payload);
            var content = await result.Content.ReadAsStringAsync();

            return _jsonSerializer.Deserialize<RpcResult<T>>(content);
        }

        private StringContent GetPayload(object data)
            => new StringContent(_jsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
    }
}