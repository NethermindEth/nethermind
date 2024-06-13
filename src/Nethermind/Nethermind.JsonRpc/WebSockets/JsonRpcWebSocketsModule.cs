// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class JsonRpcWebSocketsModule : IWebSocketsModule
{
    private readonly ConcurrentDictionary<string, ISocketsClient> _clients = new();

    private readonly JsonRpcProcessor _jsonRpcProcessor;
    private readonly IJsonRpcService _jsonRpcService;
    private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
    private readonly ILogManager _logManager;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IJsonRpcUrlCollection _jsonRpcUrlCollection;
    private readonly IRpcAuthentication _rpcAuthentication;
    private readonly long? _maxBatchResponseBodySize;

    public string Name { get; } = "json-rpc";

    public JsonRpcWebSocketsModule(JsonRpcProcessor jsonRpcProcessor,
        IJsonRpcService jsonRpcService,
        IJsonRpcLocalStats jsonRpcLocalStats,
        ILogManager logManager,
        IJsonSerializer jsonSerializer,
        IJsonRpcUrlCollection jsonRpcUrlCollection,
        IRpcAuthentication rpcAuthentication,
        long? maxBatchResponseBodySize)
    {
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcService = jsonRpcService;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _logManager = logManager;
        _jsonSerializer = jsonSerializer;
        _jsonRpcUrlCollection = jsonRpcUrlCollection;
        _rpcAuthentication = rpcAuthentication;
        _maxBatchResponseBodySize = maxBatchResponseBodySize;
    }

    public async ValueTask<ISocketsClient> CreateClient(WebSocket webSocket, string clientName, HttpContext context)
    {
        int port = context.Connection.LocalPort;

        if (!_jsonRpcUrlCollection.TryGetValue(port, out JsonRpcUrl jsonRpcUrl) || !jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Ws))
        {
            throw new InvalidOperationException($"WebSocket-enabled url not defined for port {port}");
        }

        bool authenticated = await _rpcAuthentication.Authenticate(context.Request.Headers.Authorization);

        if (jsonRpcUrl.IsAuthenticated && !authenticated)
        {
            throw new InvalidOperationException($"WebSocket connection on port {port} should be authenticated");
        }

        JsonRpcSocketsClient<WebSocketMessageStream>? socketsClient = new(
            clientName,
            new WebSocketMessageStream(webSocket, _logManager),
            RpcEndpoint.Ws,
            _jsonRpcProcessor,
            _jsonRpcLocalStats,
            _jsonSerializer,
            jsonRpcUrl,
            _maxBatchResponseBodySize);

        _clients.TryAdd(socketsClient.Id, socketsClient);

        return socketsClient;
    }

    public void RemoveClient(string id)
    {
        if (_clients.TryRemove(id, out ISocketsClient? client))
        {
            client.TryDispose();
        }
    }

    public Task SendAsync(SocketsMessage message) => Task.CompletedTask;
}
