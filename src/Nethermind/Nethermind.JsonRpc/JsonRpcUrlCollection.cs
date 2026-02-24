// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc;

public class JsonRpcUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
{
    private readonly ILogger _logger;
    private readonly IJsonRpcConfig _jsonRpcConfig;

    public JsonRpcUrlCollection(ILogManager logManager, IJsonRpcConfig jsonRpcConfig, bool includeWebSockets)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

        if (_jsonRpcConfig.Enabled)
        {
            BuildUrls(includeWebSockets);
        }
    }

    public string[] Urls => Values.Select(static x => x.ToString()).ToArray();

    private void BuildUrls(bool includeWebSockets)
    {
        bool hasEngineApi = _jsonRpcConfig.EnabledModules.Any(static m => m.Equals(ModuleType.Engine, StringComparison.OrdinalIgnoreCase));
        long? maxRequestBodySize = hasEngineApi ? SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API : _jsonRpcConfig.MaxRequestBodySize;

        RpcEndpoint defaultEndpoint = RpcEndpoint.Http;
        if (includeWebSockets && _jsonRpcConfig.WebSocketsPort == _jsonRpcConfig.Port)
        {
            defaultEndpoint |= RpcEndpoint.Ws;
        }

        JsonRpcUrl defaultUrl = new(Uri.UriSchemeHttp, _jsonRpcConfig.Host, _jsonRpcConfig.Port, defaultEndpoint, hasEngineApi, _jsonRpcConfig.EnabledModules, maxRequestBodySize);

        Add(defaultUrl.Port, defaultUrl);

        if (includeWebSockets && _jsonRpcConfig.WebSocketsPort != _jsonRpcConfig.Port)
        {
            JsonRpcUrl defaultWebSocketUrl = new(Uri.UriSchemeHttp, _jsonRpcConfig.Host, _jsonRpcConfig.WebSocketsPort, RpcEndpoint.Ws, hasEngineApi, _jsonRpcConfig.EnabledModules, maxRequestBodySize);
            Add(defaultWebSocketUrl.Port, defaultWebSocketUrl);
        }

        BuildEngineUrls(includeWebSockets);

        BuildAdditionalUrls(includeWebSockets);
    }

    private void BuildEngineUrls(bool includeWebSockets)
    {
        if (_jsonRpcConfig.EnginePort is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_jsonRpcConfig.EngineHost)) // by default EngineHost is not null
        {
            if (_logger.IsWarn) _logger.Warn("Json RPC EngineHost is set to null, " +
                "please set it to 127.0.0.1 if your CL Client is on the same machine " +
                "or to 0.0.0.0 if your CL Client is on a separate machine");
            return;
        }
        RpcEndpoint endpoint = RpcEndpoint.Http;
        if (includeWebSockets)
        {
            endpoint |= RpcEndpoint.Ws;
        }

        JsonRpcUrl url = new(Uri.UriSchemeHttp, _jsonRpcConfig.EngineHost, _jsonRpcConfig.EnginePort.Value,
            endpoint, true, _jsonRpcConfig.EngineEnabledModules.Append(ModuleType.Engine).ToArray(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);

        if (ContainsKey(url.Port))
        {
            if (_logger.IsWarn) _logger.Warn($"Execution Engine wants port {url.Port}, but port already in use; skipping...");
            return;
        }

        Add(url.Port, url);
    }

    private void BuildAdditionalUrls(bool includeWebSockets)
    {
        foreach (string additionalRpcUrl in _jsonRpcConfig.AdditionalRpcUrls)
        {
            try
            {
                JsonRpcUrl url = JsonRpcUrl.Parse(additionalRpcUrl);
                if (!includeWebSockets && url.RpcEndpoint.HasFlag(RpcEndpoint.Ws))
                {
                    RpcEndpoint endpointWithoutWs = url.RpcEndpoint & ~RpcEndpoint.Ws;
                    if (endpointWithoutWs == RpcEndpoint.None)
                    {
                        if (_logger.IsInfo) _logger.Info($"Additional JSON RPC URL '{url}' has web socket endpoint type and web sockets are not enabled; skipping...");
                        continue;
                    }

                    url = new JsonRpcUrl(url.Scheme, url.Host, url.Port, endpointWithoutWs, url.IsAuthenticated, url.EnabledModules.ToArray(), url.MaxRequestBodySize);
                }

                if (url.IsModuleEnabled(ModuleType.Engine) && _jsonRpcConfig.EnginePort is not null &&
                    !string.IsNullOrWhiteSpace(_jsonRpcConfig.EngineHost))
                {
                    if (_logger.IsInfo) _logger.Info($"EngineUrl specified. EnginePort {_jsonRpcConfig.EnginePort} EngineHost {_jsonRpcConfig.EngineHost}. Additional JSON RPC URL '{url}' has engine module enabled. skipping...");
                    continue;
                }

                if (ContainsKey(url.Port))
                {
                    if (_logger.IsInfo) _logger.Info($"Additional JSON RPC URL '{url}' wants port {url.Port}, but port already in use; skipping...");
                }
                else
                {
                    Add(url.Port, url);
                }
            }
            catch (FormatException fe)
            {
                if (_logger.IsInfo) _logger.Info($"Additional JSON RPC URL packed value '{additionalRpcUrl}' format error: {fe.Message}; skipping...");
            }
        }
    }
}

