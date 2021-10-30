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
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;
using Nethermind.Config;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcUrlCollection : IJsonRpcUrlCollection
    {
        private const string NethermindUrlVariable = "NETHERMIND_URL";

        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly List<JsonRpcUrl> _urls;

        public JsonRpcUrlCollection(ILogManager logManager, IJsonRpcConfig jsonRpcConfig, bool includeWebSockets)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _urls = new List<JsonRpcUrl>();

            BuildUrls(includeWebSockets);
        }

        public JsonRpcUrl this[int index] => _urls[index];
        public int Count => _urls.Count;
        public IEnumerator<JsonRpcUrl> GetEnumerator() => _urls.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IEnumerable<string> UrlValues => this.Select(x => x.ToString());

        private void BuildUrls(bool includeWebSockets)
        {
            JsonRpcUrl defaultUrl = new JsonRpcUrl("http", _jsonRpcConfig.Host, _jsonRpcConfig.Port, RpcEndpoint.Http, _jsonRpcConfig.EnabledModules);
            string environmentVariableUrl = Environment.GetEnvironmentVariable(NethermindUrlVariable);
            if (!string.IsNullOrWhiteSpace(environmentVariableUrl))
            {
                if (Uri.TryCreate(environmentVariableUrl, UriKind.Absolute, out Uri? uri))
                {
                    defaultUrl.Scheme = uri.Scheme;
                    defaultUrl.Host = uri.Host;
                    defaultUrl.Port = !uri.IsDefaultPort ? uri.Port : defaultUrl.Port;
                }
                else if (_logger.IsWarn)
                    _logger.Warn($"Environment variable '{NethermindUrlVariable}' value '{environmentVariableUrl}' is not valid JSON RPC URL, using default url : '{defaultUrl}'");
            }
            _urls.Add(defaultUrl);

            if (includeWebSockets)
            {
                if (_jsonRpcConfig.WebSocketsPort != _jsonRpcConfig.Port)
                {
                    JsonRpcUrl defaultWebSocketUrl = defaultUrl.Clone() as JsonRpcUrl;
                    defaultWebSocketUrl.Port = _jsonRpcConfig.Port;
                    defaultWebSocketUrl.RpcEndpoint = RpcEndpoint.WebSocket;
                    _urls.Add(defaultWebSocketUrl);
                }
                else
                    defaultUrl.RpcEndpoint |= RpcEndpoint.WebSocket;
            }

            foreach (string additionalRpcUrl in _jsonRpcConfig.AdditionalRPCUrls)
            {
                try
                {
                    JsonRpcUrl url = JsonRpcUrl.Parse(additionalRpcUrl);
                    if (!includeWebSockets && url.RpcEndpoint.HasFlag(RpcEndpoint.WebSocket))
                        continue;

                    _urls.Add(url);
                }
                catch (Exception)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Additional JSON RPC URL packed value '{additionalRpcUrl}' is not formatted correctly, skipping...");
                    continue;
                }
            }
        }
    }
}

