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
using System.Threading.Tasks;

namespace Nethermind.Facade.Proxy
{
    public class JsonRpcClientProxy : IJsonRpcClientProxy
    {
        private readonly IHttpClient _client;
        private readonly string _url;

        public JsonRpcClientProxy(IHttpClient client, string[] urlProxies)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            if (urlProxies is null)
            {
                throw new ArgumentNullException(nameof(urlProxies));
            }

            if (!urlProxies.Any())
            {
                throw new ArgumentException("Empty JSON RPC URL proxies.", nameof(urlProxies));
            }

            foreach (var url in urlProxies)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new ArgumentException("Empty JSON RPC URL proxy.", nameof(_url));
                }

                new Uri(url);
            }

            _url = urlProxies.FirstOrDefault();
        }

        public Task<RpcResult<T>> SendAsync<T>(string method, params object[] @params)
            => SendAsync<T>(method, 1, @params);

        public Task<RpcResult<T>> SendAsync<T>(string method, long id, params object[] @params)
            => _client.PostJsonAsync<RpcResult<T>>(_url, new
            {
                jsonrpc = 2.0,
                id,
                method,
                @params = (@params ?? Array.Empty<object>()).Where(x => !(x is null))
            });
    }
}