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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
    {
        private const string NethermindUrlVariable = "NETHERMIND_URL";

        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        public JsonRpcUrlCollection(ILogManager logManager, IJsonRpcConfig jsonRpcConfig, bool includeWebSockets)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

            if (_jsonRpcConfig.Enabled)
                BuildUrls(includeWebSockets);
        }

        public string[] Urls => Values.Select(x => x.ToString()).ToArray();

        private void BuildUrls(bool includeWebSockets)
        {
            bool isAuthenticated = _jsonRpcConfig.EnabledModules.Any(m => m.ToLower() == "engine");
            JsonRpcUrl defaultUrl = new JsonRpcUrl(Uri.UriSchemeHttp, _jsonRpcConfig.Host, _jsonRpcConfig.Port, RpcEndpoint.Http, isAuthenticated, _jsonRpcConfig.EnabledModules);
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
            Add(defaultUrl.Port, defaultUrl);

            if (includeWebSockets)
            {
                if (_jsonRpcConfig.WebSocketsPort != _jsonRpcConfig.Port)
                {
                    JsonRpcUrl defaultWebSocketUrl = defaultUrl.Clone() as JsonRpcUrl;
                    defaultWebSocketUrl.Port = _jsonRpcConfig.WebSocketsPort;
                    defaultWebSocketUrl.RpcEndpoint = RpcEndpoint.Ws;
                    Add(defaultWebSocketUrl.Port, defaultWebSocketUrl);
                }
                else
                    defaultUrl.RpcEndpoint |= RpcEndpoint.Ws;
            }

            foreach (string additionalRpcUrl in _jsonRpcConfig.AdditionalRpcUrls)
            {
                try
                {
                    JsonRpcUrl url = JsonRpcUrl.Parse(additionalRpcUrl);
                    if (!includeWebSockets && url.RpcEndpoint.HasFlag(RpcEndpoint.Ws))
                    {
                        url.RpcEndpoint &= ~RpcEndpoint.Ws;
                        if (url.RpcEndpoint == RpcEndpoint.None)
                        {
                            if (_logger.IsInfo)
                                _logger.Info($"Additional JSON RPC URL '{url}' has web socket endpoint type and web sockets are not enabled; skipping...");
                            continue;
                        }
                    }

                    if (ContainsKey(url.Port))
                    {
                        if (_logger.IsInfo)
                            _logger.Info($"Additional JSON RPC URL '{url}' wants port {url.Port}, but port already in use; skipping...");
                        continue;
                    }

                    Add(url.Port, url);
                }
                catch (FormatException fe)
                {
                    if (_logger.IsInfo)
                        _logger.Info($"Additional JSON RPC URL packed value '{additionalRpcUrl}' format error: {fe.Message}; skipping...");
                    continue;
                }
            }
        }
    }
}

