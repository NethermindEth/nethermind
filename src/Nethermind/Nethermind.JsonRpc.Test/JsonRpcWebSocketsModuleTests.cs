// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
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
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class JsonRpcWebSocketsModuleTests
{
    private const int PublicConcurrency = 16;

    [TestCase(true, true, 1, TestName = "GetProcessingConcurrency_AuthenticatedEngineUrl_ProcessesOneAtATime")]
    [TestCase(false, true, 1, TestName = "GetProcessingConcurrency_EngineUrlWithoutAuth_ProcessesOneAtATime")]
    [TestCase(false, false, PublicConcurrency, TestName = "GetProcessingConcurrency_PublicUrl_UsesTheConfiguredConcurrency")]
    public void GetProcessingConcurrency_FollowsTheUrl(bool isAuthenticated, bool engine, int expected)
    {
        JsonRpcUrl url = CreateUrl(isAuthenticated, engine);

        int concurrency = CreateModule(url, Substitute.For<IJsonRpcProcessor>()).GetProcessingConcurrency(url);

        Assert.That(concurrency, Is.EqualTo(expected),
            "the engine API needs its pipelined requests run in order; only public connections run them concurrently");
    }

    [Test]
    public async Task CreateClient_EngineConnection_ProcessesPipelinedRequestsInOrder()
    {
        // 1. An authenticated engine connection pipelines a slow request (newPayload), then a fast one (forkchoiceUpdated).
        // 2. The slow request is held until the fast one has been sent, giving a second worker every chance to start it.
        // 3. The fast request must start only after the slow one has answered.
        TaskCompletionSource fastSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingProcessor processor = new(slowGate: fastSent.Task);
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

        Assert.That(processor.Events, Is.EqualTo(new[] { "slow start", "slow end", "fast start", "fast end" }),
            "an engine connection must run a pipelined request only after the one before it has answered");
    }

    [Test]
    public async Task CreateClient_PublicConnection_AnswersAFastRequestBehindASlowOne()
    {
        // 1. A public connection pipelines a slow request, then a fast one.
        // 2. The slow request is held until the fast one has answered.
        // 3. Processing one request at a time would never answer the fast request, so the bounded wait fails.
        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingProcessor processor = new(slowGate: releaseSlow.Task);
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

    private sealed class RecordingProcessor
    {
        private readonly List<string> _events = [];
        private readonly Task _slowGate;
        private readonly TaskCompletionSource _slowAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fastAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingProcessor(Task slowGate)
        {
            _slowGate = slowGate;
            Processor = Substitute.For<IJsonRpcProcessor>();
            Processor
                .ProcessAsync(
                    Arg.Any<PipeReader>(),
                    Arg.Any<JsonRpcContext>(),
                    Arg.Any<IJsonRpcResponseSink>(),
                    Arg.Any<JsonRpcProcessingOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Process);
        }

        public IJsonRpcProcessor Processor { get; }
        public Task SlowAnswered => _slowAnswered.Task;
        public Task FastAnswered => _fastAnswered.Task;

        public string[] Events
        {
            get
            {
                lock (_events) return [.. _events];
            }
        }

        private async ValueTask Process(CallInfo call)
        {
            PipeReader reader = call.ArgAt<PipeReader>(0);
            ReadResult read = await reader.ReadToEndAsync();
            bool slow = read.Buffer.FirstSpan.SequenceEqual("slow"u8);
            reader.AdvanceTo(read.Buffer.End);
            string name = slow ? "slow" : "fast";

            Record($"{name} start");
            if (slow) await _slowGate;

            IJsonRpcResponseSink sink = call.Arg<IJsonRpcResponseSink>();
            await sink.WriteSingleAsync(new JsonRpcSuccessResponse(null), new RpcReport(), call.Arg<CancellationToken>());
            Record($"{name} end");
            (slow ? _slowAnswered : _fastAnswered).TrySetResult();
        }

        private void Record(string entry)
        {
            lock (_events) _events.Add(entry);
        }
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
            await clientSocket.ConnectAsync(listener.LocalEndPoint!, token);
            Socket serverSocket = await listener.AcceptAsync(token);

            WebSocket client = WebSocket.CreateFromStream(new NetworkStream(clientSocket, ownsSocket: true), new WebSocketCreationOptions());
            WebSocket server = WebSocket.CreateFromStream(new NetworkStream(serverSocket, ownsSocket: true), new WebSocketCreationOptions { IsServer = true });

            DefaultHttpContext context = new();
            context.Connection.LocalPort = url.Port;
            context.Request.Headers.Authorization = "Bearer test";
            ISocketsClient socketsClient = await CreateModule(url, processor).CreateClient(server, "test", context);

            CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task receiveLoop = Task.Run(() => socketsClient.ReceiveLoopAsync(stop.Token), CancellationToken.None);
            return new WsConnection(client, socketsClient, receiveLoop, stop);
        }

        public Task SendAsync(string message, CancellationToken token) =>
            _client.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }

            _server.Dispose();
            _client.Dispose();
            _stop.Dispose();
        }
    }
}
