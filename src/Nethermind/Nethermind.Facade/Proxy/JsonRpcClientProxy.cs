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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Facade.Proxy
{
    public class JsonRpcClientProxy : IJsonRpcClientProxy
    {
        private readonly IHttpClient _client;
        private string _url;
        private readonly ILogger _logger;

        public JsonRpcClientProxy(IHttpClient client, IEnumerable<string> urls, ILogManager logManager)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            if (HasEmptyUrls(urls))
            {
                return;
            }
            
            UpdateUrls(urls.ToArray());
        }

        public Task<RpcResult<T>> SendAsync<T>(string method, params object[] @params)
            => SendAsync<T>(method, 1, @params);

        public Task<RpcResult<T>> SendAsync<T>(string method, long id, params object[] @params)
        {
            if (string.IsNullOrWhiteSpace(_url))
            {
                if (_logger.IsWarn) _logger.Warn("JSON RPC Proxy URL isn't specified - call will not be executed.");
                return Task.FromResult<RpcResult<T>>(default);
            }

            return _client.PostJsonAsync<RpcResult<T>>(_url, new
            {
                jsonrpc = 2.0,
                id,
                method,
                @params = (@params ?? Array.Empty<object>()).Where(x => !(x is null))
            });
        }

        // Multiple URLs as a possibility for load-balancing/fallback mechanism in the future.
        public void SetUrls(params string[] urls)
        {
            if (UpdateUrls(urls)) return;
            if (_logger.IsInfo) _logger.Info("JSON RPC Proxy URL has been set.");
        }

        private bool UpdateUrls(string[] urls)
        {
            if (HasEmptyUrls(urls))
            {
                _url = string.Empty;
                if (_logger.IsWarn) _logger.Warn("JSON RPC Proxy URL has been removed.");
                return true;
            }

            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                new Uri(url);
            }

            _url = urls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            return false;
        }

        private static bool HasEmptyUrls(IEnumerable<string> urls)
            => urls is null || !urls.Any() || urls.All(string.IsNullOrWhiteSpace);
    }
}
