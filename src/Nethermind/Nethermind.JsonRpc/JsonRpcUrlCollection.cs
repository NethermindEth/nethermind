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

    public string[] Urls => Values.Select(x => x.ToString()).ToArray();

    private void BuildUrls(bool includeWebSockets)
    {
        bool hasEngineApi = _jsonRpcConfig.EnabledModules.Any(m => m.Equals(ModuleType.Engine, StringComparison.OrdinalIgnoreCase));
        long? maxRequestBodySize = hasEngineApi ? SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API : _jsonRpcConfig.MaxRequestBodySize;
        JsonRpcUrl defaultUrl = new(Uri.UriSchemeHttp, _jsonRpcConfig.Host, _jsonRpcConfig.Port, RpcEndpoint.Http, hasEngineApi, _jsonRpcConfig.EnabledModules, maxRequestBodySize);

        Add(defaultUrl.Port, defaultUrl);

        if (includeWebSockets)
        {
            if (_jsonRpcConfig.WebSocketsPort != _jsonRpcConfig.Port)
            {
                JsonRpcUrl defaultWebSocketUrl = (JsonRpcUrl)defaultUrl.Clone();
                defaultWebSocketUrl.Port = _jsonRpcConfig.WebSocketsPort;
                defaultWebSocketUrl.RpcEndpoint = RpcEndpoint.Ws;
                Add(defaultWebSocketUrl.Port, defaultWebSocketUrl);
            }
            else
            {
                defaultUrl.RpcEndpoint |= RpcEndpoint.Ws;
            }
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
        JsonRpcUrl url = new(Uri.UriSchemeHttp, _jsonRpcConfig.EngineHost, _jsonRpcConfig.EnginePort.Value,
            RpcEndpoint.Http, true, _jsonRpcConfig.EngineEnabledModules.Append(ModuleType.Engine).ToArray(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);

        if (ContainsKey(url.Port))
        {
            if (_logger.IsWarn) _logger.Warn($"Execution Engine wants port {url.Port}, but port already in use; skipping...");
            return;
        }

        if (includeWebSockets)
        {
            url.RpcEndpoint |= RpcEndpoint.Ws;
        }

        Add(url.Port, url);
    }

    private void BuildAdditionalUrls(bool includeWebSockets)
    {
        List<string[]> additionalRpcUrlRows = new List<string[]>();
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
                        if (_logger.IsInfo) _logger.Info($"Additional JSON RPC URL '{url}' has web socket endpoint type and web sockets are not enabled; skipping...");
                        continue;
                    }
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
                    string[] row = new[]
                    {
                        $"{url.Host}:{url.Port}",
                        url.RpcEndpoint.ToString(),
                        string.Join(", ", url.EnabledModules)
                    };
                    additionalRpcUrlRows.Add(row);
                }
            }
            catch (FormatException fe)
            {
                if (_logger.IsInfo) _logger.Info($"Additional JSON RPC URL packed value '{additionalRpcUrl}' format error: {fe.Message}; skipping...");
            }
        }
        LogTable(additionalRpcUrlRows);
    }
    private void LogTable(List<string[]> rows)
    {
        
        string[] headers = ["Additional Url", "Protocols", "Modules"];
        string redSeparator = "\u001b[31m|\u001b[0m"; 
        var columnWidths = new int[headers.Length];

        for (int i = 0; i < headers.Length; i++)
        {
            columnWidths[i] = headers[i].Length;
            foreach (var row in rows)
            {
                if (i < row.Length)
                {
                    columnWidths[i] = Math.Max(columnWidths[i], row[i]?.Length ?? 0);
                }
            }
        }

        var separator = "-" + string.Join("-", columnWidths.Select(width => new string('-', width + 2))) + "-";

        _logger.Info("\u001b[31m*****\u001b[0m Additional RPC URLs \u001b[31m*****\u001b[0m");
        _logger.Info(separator);
        _logger.Info(redSeparator + " " + string.Join(" " + redSeparator + " ", headers.Select((h, i) => h.PadRight(columnWidths[i]))) + " " + redSeparator);
        _logger.Info(separator);

        foreach (var row in rows)
        {
            _logger.Info(redSeparator + " " + string.Join(" " + redSeparator + " ", row.Select((cell, i) => (cell ?? "").PadRight(columnWidths[i]))) + " " + redSeparator);
        }

        _logger.Info(separator);
    }
}

