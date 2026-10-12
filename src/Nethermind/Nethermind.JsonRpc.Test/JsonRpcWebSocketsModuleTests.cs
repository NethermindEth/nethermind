// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class JsonRpcWebSocketsModuleTests
{
    private const int PublicConcurrency = 16;

    [Test]
    public async Task CreateClient_EngineConnection_ProcessesPipelinedRequestsInOrder()
    {
        // 1. An authenticated engine connection pipelines a slow request (newPayload), then a fast one (forkchoiceUpdated).
        // 2. The slow request is held until the fast one has been sent, giving a second worker every chance to start it.
        // 3. The fast request must start only after the slow one has answered.
        TaskCompletionSource fastSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelinedRequestRecorder processor = new(slowGate: fastSent.Task);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using (WsConnection connection = await WsConnection.OpenAsync(CreateUrl(isAuthenticated: true, engine: true), processor.Processor, cts.Token))
        {
            try
            {
                await connection.SendAsync("slow", cts.Token);
                await connection.SendAsync("fast", cts.Token);
            }
            finally
            {
                fastSent.TrySetResult();
            }

            await Task.WhenAll(processor.SlowAnswered, processor.FastAnswered).WaitAsync(cts.Token);
        }

        Assert.That(processor.Events, Is.EqualTo(PipelinedRequestRecorder.InOrder),
            "an engine connection must run a pipelined request only after the one before it has answered");
    }

    [Test]
    public async Task CreateClient_PublicConnection_AnswersAFastRequestBehindASlowOne()
    {
        // 1. A public connection pipelines a slow request, then a fast one.
        // 2. The slow request is held until the fast one has answered.
        // 3. Processing one request at a time would never answer the fast request, so the bounded wait fails.
        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelinedRequestRecorder processor = new(slowGate: releaseSlow.Task);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using WsConnection connection = await WsConnection.OpenAsync(CreateUrl(isAuthenticated: false, engine: false), processor.Processor, cts.Token);
        try
        {
            await connection.SendAsync("slow", cts.Token);
            await connection.SendAsync("fast", cts.Token);
            Assert.That(async () => await processor.FastAnswered.WaitAsync(cts.Token), Throws.Nothing,
                "a slow request must not block a later request on a public connection");
        }
        finally
        {
            releaseSlow.TrySetResult();
        }

        await processor.SlowAnswered.WaitAsync(cts.Token);
    }

    private static JsonRpcUrl CreateUrl(bool isAuthenticated, bool engine) =>
        new("http", "127.0.0.1", 8551, RpcEndpoint.Http | RpcEndpoint.Ws, isAuthenticated,
            engine ? [ModuleType.Engine, ModuleType.Eth] : [ModuleType.Eth]);

    private static JsonRpcWebSocketsModule CreateModule(JsonRpcUrl url, IJsonRpcProcessor processor)
    {
        IRpcAuthentication authentication = Substitute.For<IRpcAuthentication>();
        authentication.Authenticate(Arg.Any<string>()).Returns(true);

        return new JsonRpcWebSocketsModule(
            processor,
            Substitute.For<IJsonRpcService>(),
            new NullJsonRpcLocalStats(),
            LimboLogs.Instance,
            new EthereumJsonSerializer(),
            new SingleUrlCollection(url),
            authentication,
            maxBatchResponseBodySize: null,
            PublicConcurrency);
    }

    private sealed class SingleUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
    {
        public SingleUrlCollection(JsonRpcUrl url) => Add(url.Port, url);

        public string[] Urls => [];
    }

    private sealed class WsConnection : IAsyncDisposable
    {
        private readonly WebSocket _client;
        private readonly ISocketsClient _server;
        private readonly Task _receiveLoop;
        private readonly CancellationTokenSource _stop;

        private WsConnection(WebSocket client, ISocketsClient server, Task receiveLoop, CancellationTokenSource stop)
        {
            _client = client;
            _server = server;
            _receiveLoop = receiveLoop;
            _stop = stop;
        }

        public static async Task<WsConnection> OpenAsync(JsonRpcUrl url, IJsonRpcProcessor processor, CancellationToken token)
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Socket? serverSocket = null;
            WebSocket? client = null;
            WebSocket? server = null;
            try
            {
                await clientSocket.ConnectAsync(listener.LocalEndPoint!, token);
                serverSocket = await listener.AcceptAsync(token);

                client = WebSocket.CreateFromStream(new NetworkStream(clientSocket, ownsSocket: true), new WebSocketCreationOptions());
                server = WebSocket.CreateFromStream(new NetworkStream(serverSocket, ownsSocket: true), new WebSocketCreationOptions { IsServer = true });

                DefaultHttpContext context = new();
                context.Connection.LocalPort = url.Port;
                context.Request.Headers.Authorization = "Bearer test";
                ISocketsClient socketsClient = await CreateModule(url, processor).CreateClient(server, "test", context);

                CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task receiveLoop = Task.Run(() => socketsClient.ReceiveLoopAsync(stop.Token), CancellationToken.None);
                return new WsConnection(client, socketsClient, receiveLoop, stop);
            }
            catch
            {
                client?.Dispose();
                server?.Dispose();
                clientSocket.Dispose();
                serverSocket?.Dispose();
                throw;
            }
        }

        public Task SendAsync(string message, CancellationToken token) =>
            _client.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, token);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stop.CancelAsync();
                await _receiveLoop;
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
            {
                await TestContext.Out.WriteLineAsync($"Receive loop stopped on shutdown: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _server.Dispose();
                _client.Dispose();
                _stop.Dispose();
            }
        }
    }
}
