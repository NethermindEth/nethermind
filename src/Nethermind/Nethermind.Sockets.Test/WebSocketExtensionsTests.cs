// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Sockets.Test;

public class WebSocketExtensionsTests
{
    private class WebSocketMock(Queue<WebSocketReceiveResult> receiveResults) : WebSocket
    {
        private readonly Queue<WebSocketReceiveResult> _receiveResults = receiveResults;

        public override void Abort() => throw new NotImplementedException();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();

        public override void Dispose() => throw new NotImplementedException();

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            LargestReceiveBuffer = Math.Max(LargestReceiveBuffer, buffer.Count);
            // Had to use Array.Fill as it is more performant
            Array.Fill(buffer.Array, (byte)0, buffer.Offset, buffer.Count);
            Payload?.CopyTo(buffer.Array, buffer.Offset);

            if (_receiveResults.Count == 0 && ReturnTaskWithFaultOnEmptyQueue)
            {
                Task<WebSocketReceiveResult> a = new(static () => throw new Exception());
                a.Start();
                return a;
            }

            return Task.FromResult(_receiveResults.Dequeue());
        }

        public long SentBytes { get; private set; }
        public int SentEndMessages { get; private set; }
        public int LargestReceiveBuffer { get; private set; }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentBytes += buffer.Count;
            if (endOfMessage)
            {
                SentEndMessages++;
            }

            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus { get; }
        public override string CloseStatusDescription { get; }
        public override WebSocketState State => CurrentState;
        public WebSocketState CurrentState { get; set; } = WebSocketState.Open;
        public override string SubProtocol { get; }
        public bool ReturnTaskWithFaultOnEmptyQueue { get; set; }
        public byte[] Payload { get; init; }
    }

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task Can_receive_whole_message()
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        receiveResult.Enqueue(new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false));
        receiveResult.Enqueue(new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false));
        receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult);

        SocketClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            Substitute.For<IJsonSerializer>(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API,
            false);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);
        await webSocketsClient.Received().ProcessAsync(Arg.Is<ArraySegment<byte>>(static ba => ba.Count == 2 * 4096 + 1024), cts.Token);
    }

    class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Test]
    public async Task Updates_Metrics_And_Stats_Successfully()
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult);

        IJsonRpcProcessor processor = Substitute.For<IJsonRpcProcessor>();
        static async ValueTask WriteResponses(CallInfo callInfo)
        {
            IJsonRpcResponseSink sink = callInfo.Arg<IJsonRpcResponseSink>();
            CancellationToken cancellationToken = callInfo.Arg<CancellationToken>();

            await sink.WriteSingleAsync(new JsonRpcResponse(), new RpcReport("single", 0, true), cancellationToken);
            await sink.BeginBatchAsync(cancellationToken);
            for (int index = 0; index < 3; index++)
            {
                await sink.WriteBatchItemAsync(new JsonRpcResponse(), new RpcReport("batch", 0, true), cancellationToken);
            }
            await sink.EndBatchAsync(cancellationToken);
        }

        processor
            .ProcessAsync(
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<JsonRpcContext>(),
                Arg.Any<IJsonRpcResponseSink>(),
                Arg.Any<JsonRpcProcessingOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(WriteResponses);

        IJsonRpcService service = Substitute.For<IJsonRpcService>();

        IJsonRpcLocalStats localStats = Substitute.For<IJsonRpcLocalStats>();
        localStats.IsEnabled.Returns(true);
        long receivedBefore = Metrics.JsonRpcBytesReceivedWebSockets;
        long sentBefore = Metrics.JsonRpcBytesSentWebSockets;

        JsonRpcSocketsClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<JsonRpcSocketsClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            RpcEndpoint.Ws,
            processor,
            localStats,
            new EthereumJsonSerializer(),
            null,
            30.MB,
            1,
            null,
            null,
            null,
            0L,
            0);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.JsonRpcBytesReceivedWebSockets - receivedBefore, Is.EqualTo(1024));
            Assert.That(Metrics.JsonRpcBytesSentWebSockets - sentBefore, Is.EqualTo(mock.SentBytes));
            Assert.That(mock.SentEndMessages, Is.EqualTo(2));
        }
        localStats.Received(1).ReportCall(Arg.Is<RpcReport>(static report => report.Method != "# collection serialization #"), Arg.Any<long>(), Arg.Is<long>(static size => size > 0));
        localStats.Received(1).ReportCall(Arg.Is<RpcReport>(static report => report.Method == "# collection serialization #"), Arg.Any<long>(), Arg.Is<long>(static size => size > 0));
        localStats.Received(3).ReportCall(Arg.Any<RpcReport>());
    }

    [TestCase(null, " from 203.0.113.7")]
    [TestCase("198.51.100.1", " from 203.0.113.7 (X-Forwarded-For: 198.51.100.1)")]
    public async Task Created_client_logs_caller_address(string forwardedFor, string expectedFragment)
    {
        const string secret = "private-rpc-parameter";
        const string request = "{\"params\":[\"" + secret + "\"]";
        Queue<WebSocketReceiveResult> receiveResult = new();
        receiveResult.Enqueue(new WebSocketReceiveResult(request.Length, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult) { Payload = Encoding.UTF8.GetBytes(request) };

        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>()).Returns(new JsonRpcErrorResponse { Error = new Error() });
        JsonRpcConfig config = new() { Enabled = true };
        JsonRpcWebSocketsModule module = new(
            new JsonRpcProcessor(service, config, Substitute.For<IFileSystem>(), new OneLoggerLogManager(new ILogger(logger))),
            service,
            Substitute.For<IJsonRpcLocalStats>(),
            LimboLogs.Instance,
            new EthereumJsonSerializer(),
            new JsonRpcUrlCollection(LimboLogs.Instance, config, true),
            Substitute.For<IRpcAuthentication>(),
            null,
            1);

        DefaultHttpContext context = new();
        context.Connection.LocalPort = config.Port;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        using ISocketsClient client = await module.CreateClient(mock, "TestClient", context);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await client.ReceiveLoopAsync(cts.Token);

        string[] messages = logger.ReceivedCalls().SelectMany(static call => call.GetArguments()).OfType<string>().ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(messages, Has.Some.Contains(expectedFragment));
            Assert.That(messages, Has.None.Contains(secret));
        }
    }

    [Test]
    public async Task Logs_receive_loop_failure_as_error_only_when_connection_was_not_aborted([Values] bool aborted)
    {
        ISocketsClient client = Substitute.For<ISocketsClient>();
        client.Id.Returns("TestClient");
        client.ReceiveLoopAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new ConnectionAbortedException()));
        IWebSocketsModule module = Substitute.For<IWebSocketsModule>();
        module.CreateClient(Arg.Any<WebSocket>(), Arg.Any<string>(), Arg.Any<HttpContext>()).Returns(client);
        IWebSocketsManager manager = Substitute.For<IWebSocketsManager>();
        manager.GetModule("ws").Returns(module);

        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsTrace.Returns(true);
        logger.IsError.Returns(true);
        ServiceCollection services = new();
        services.AddSingleton(manager);
        services.AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger)));
        ApplicationBuilder app = new(services.BuildServiceProvider());
        app.UseWebSocketsModules();

        IHttpWebSocketFeature webSocketFeature = Substitute.For<IHttpWebSocketFeature>();
        webSocketFeature.IsWebSocketRequest.Returns(true);
        webSocketFeature.AcceptAsync(Arg.Any<WebSocketAcceptContext>()).Returns(new WebSocketMock(new Queue<WebSocketReceiveResult>()));
        DefaultHttpContext context = new();
        context.Features.Set(webSocketFeature);
        context.Request.Path = "/ws";
        using CancellationTokenSource requestAborted = new();
        context.RequestAborted = requestAborted.Token;
        if (aborted) await requestAborted.CancelAsync();

        await app.Build()(context);

        logger.Received(aborted ? 0 : 1).Error(Arg.Any<string>(), Arg.Any<Exception>());
        module.Received(1).RemoveClient("TestClient");
    }

    [Test]
    public async Task Logs_send_limit_close_at_debug_level()
    {
        ISocketsClient client = Substitute.For<ISocketsClient>();
        client.Id.Returns("TestClient");
        client.ReceiveLoopAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new SocketSendLimitException("limit")));
        IWebSocketsModule module = Substitute.For<IWebSocketsModule>();
        module.CreateClient(Arg.Any<WebSocket>(), Arg.Any<string>(), Arg.Any<HttpContext>()).Returns(client);
        IWebSocketsManager manager = Substitute.For<IWebSocketsManager>();
        manager.GetModule("ws").Returns(module);

        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        logger.IsError.Returns(true);
        ServiceCollection services = new();
        services.AddSingleton(manager);
        services.AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger)));
        ApplicationBuilder app = new(services.BuildServiceProvider());
        app.UseWebSocketsModules();

        IHttpWebSocketFeature webSocketFeature = Substitute.For<IHttpWebSocketFeature>();
        webSocketFeature.IsWebSocketRequest.Returns(true);
        webSocketFeature.AcceptAsync(Arg.Any<WebSocketAcceptContext>()).Returns(Substitute.For<WebSocket>());
        DefaultHttpContext context = new();
        context.Features.Set(webSocketFeature);
        context.Request.Path = "/ws";

        await app.Build()(context);

        logger.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
        logger.Received(1).Debug(Arg.Is<string>(static message => message.Contains("limit")));
        module.Received(1).RemoveClient("TestClient");
    }

    [Test]
    public async Task Can_receive_many_messages()
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        for (int i = 0; i < 1000; i++)
        {
            receiveResult.Enqueue(new WebSocketReceiveResult(1234, WebSocketMessageType.Text, true));
        }

        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        WebSocketMock mock = new(receiveResult);
        SocketClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            Substitute.For<IJsonSerializer>(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API,
            false);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);
        await webSocketsClient.Received(1000).ProcessAsync(Arg.Is<ArraySegment<byte>>(static ba => ba.Count == 1234), cts.Token);
    }

    [Test]
    public async Task Empty_messages_are_processed_only_when_enabled([Values] bool processEmptyMessages)
    {
        Queue<WebSocketReceiveResult> receiveResults = new([
            new(0, WebSocketMessageType.Text, true),
            new(1, WebSocketMessageType.Text, true),
            new(0, WebSocketMessageType.Close, true),
        ]);
        WebSocketMock socket = new(receiveResults);
        SocketClient<WebSocketMessageStream> client = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient", new WebSocketMessageStream(socket, NullLogManager.Instance), Substitute.For<IJsonSerializer>(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API, processEmptyMessages);

        await client.ReceiveLoopAsync(CancellationToken.None);

        await client.Received(processEmptyMessages ? 1 : 0).ProcessAsync(Arg.Is<ArraySegment<byte>>(data => data.Count == 0), CancellationToken.None);
        await client.Received(1).ProcessAsync(Arg.Is<ArraySegment<byte>>(data => data.Count == 1), CancellationToken.None);
    }

    [Test]
    public async Task Can_receive_whole_message_non_buffer_sizes()
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        for (int i = 0; i < 6; i++)
        {
            receiveResult.Enqueue(new WebSocketReceiveResult(2000, WebSocketMessageType.Text, false));
        }

        receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult);

        SocketClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            Substitute.For<IJsonSerializer>(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API,
            false);


        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);
        await webSocketsClient.Received().ProcessAsync(Arg.Is<ArraySegment<byte>>(static ba => ba.Count == 6 * 2000 + 1), cts.Token);
    }

    [Test]
    public async Task Throws_on_too_long_message([Values(64 * 1024 + 1, 1024 * 1024)] int maxRequestSize)
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        for (int i = 0; i < 2 * 1024; i++)
        {
            receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, false));
        }

        receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult);

        SocketClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            Substitute.For<IJsonSerializer>(),
            maxRequestSize,
            false);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await webSocketsClient.ReceiveLoopAsync(cts.Token));
        await webSocketsClient.DidNotReceive().ProcessAsync(Arg.Any<ArraySegment<byte>>(), cts.Token);
        byte[] maximumRental = ArrayPool<byte>.Shared.Rent(maxRequestSize);
        int maximumPooledLength = maximumRental.Length;
        ArrayPool<byte>.Shared.Return(maximumRental);
        Assert.That(mock.LargestReceiveBuffer, Is.LessThanOrEqualTo(maximumPooledLength));
    }

    [Test, MaxTime(5000)]
    public async Task Stops_on_dirty_disconnect()
    {
        Queue<WebSocketReceiveResult> receiveResult = new();
        receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        WebSocketMock mock = new(receiveResult);
        mock.ReturnTaskWithFaultOnEmptyQueue = true;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        SocketClient<WebSocketMessageStream> webSocketsClient = Substitute.ForPartsOf<SocketClient<WebSocketMessageStream>>(
            "TestClient",
            new WebSocketMessageStream(mock, Substitute.For<ILogManager>()),
            Substitute.For<IJsonSerializer>(),
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API,
            false);

        await webSocketsClient.ReceiveLoopAsync(cts.Token);
    }

    [Test]
    public async Task Writes_to_a_closed_socket_fail_the_same_way([Values] bool endOfMessage)
    {
        WebSocketMock mock = new(new Queue<WebSocketReceiveResult>()) { CurrentState = WebSocketState.Closed };
        WebSocketMessageStream stream = new(mock, Substitute.For<ILogManager>());
        ReadOnlyMemory<byte> data = "{}"u8.ToArray();

        await Assert.ThatAsync(async () =>
        {
            if (endOfMessage) await stream.WriteEndOfMessageAsync(data, CancellationToken.None);
            else await stream.WriteAsync(data, CancellationToken.None);
        }, Throws.TypeOf<IOException>());
        Assert.That(mock.SentBytes, Is.Zero);
    }

    [Test]
    public async Task Logs_an_aborted_receive_at_info_only_when_it_was_not_cancelled([Values] bool cancelled)
    {
        WebSocket socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        socket.ReceiveAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>()).Returns(
            ValueTask.FromException<ValueWebSocketReceiveResult>(new OperationCanceledException(null, new ConnectionAbortedException())));
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.IsTrace.Returns(true);
        WebSocketMessageStream stream = new(socket, new OneLoggerLogManager(new ILogger(logger)));
        using CancellationTokenSource cts = new();
        if (cancelled) await cts.CancelAsync();

        ReceiveResult result = await stream.ReceiveAsync(new byte[16], cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Closed, Is.True);
            logger.Received(cancelled ? 0 : 1).Info(Arg.Any<string>());
            logger.Received(cancelled ? 1 : 0).Trace(Arg.Any<string>());
        }
    }

    [Test]
    public async Task Shutdown_close_finishes_before_the_socket_is_disposed()
    {
        TaskCompletionSource closeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WebSocket socket = Substitute.For<WebSocket>();
        socket.CloseOutputAsync(Arg.Any<WebSocketCloseStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(closeSent.Task);
        TaskCompletionSource receiveLoop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISocketsClient client = Substitute.For<ISocketsClient>();
        client.Id.Returns("TestClient");
        client.ReceiveLoopAsync(Arg.Any<CancellationToken>()).Returns(receiveLoop.Task);
        IWebSocketsModule module = Substitute.For<IWebSocketsModule>();
        module.CreateClient(Arg.Any<WebSocket>(), Arg.Any<string>(), Arg.Any<HttpContext>()).Returns(client);
        IWebSocketsManager manager = Substitute.For<IWebSocketsManager>();
        manager.GetModule("ws").Returns(module);
        using CancellationTokenSource stopping = new();
        IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);

        ServiceCollection services = new();
        services.AddSingleton(manager);
        services.AddSingleton(lifetime);
        ApplicationBuilder app = new(services.BuildServiceProvider());
        app.UseWebSocketsModules();

        IHttpWebSocketFeature webSocketFeature = Substitute.For<IHttpWebSocketFeature>();
        webSocketFeature.IsWebSocketRequest.Returns(true);
        webSocketFeature.AcceptAsync(Arg.Any<WebSocketAcceptContext>()).Returns(socket);
        DefaultHttpContext context = new();
        context.Features.Set(webSocketFeature);
        context.Request.Path = "/ws";

        Task request = app.Build()(context);
        await stopping.CancelAsync();
        // The client closes while the close sent at shutdown is still in flight.
        receiveLoop.SetResult();
        Task first = await Task.WhenAny(request, Task.Delay(TimeSpan.FromMilliseconds(200)));
        bool disposedDuringClose = socket.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == nameof(WebSocket.Dispose));

        closeSent.SetResult();
        await request;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.SameAs(request), "the request ended while the close was being sent");
            Assert.That(disposedDuringClose, Is.False);
            socket.Received(1).Dispose();
            module.Received(1).RemoveClient("TestClient");
        }
    }

    [TestCase(WebSocketState.Open, true)]
    [TestCase(WebSocketState.CloseSent, true)]
    [TestCase(WebSocketState.CloseReceived, false)]
    [TestCase(WebSocketState.Closed, false)]
    [TestCase(WebSocketState.Aborted, false)]
    public async Task Receives_until_the_client_answers_a_close_sent_by_the_server(WebSocketState state, bool receives)
    {
        WebSocket socket = Substitute.For<WebSocket>();
        socket.State.Returns(state);
        socket.ReceiveAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>()).Returns(
            ValueTask.FromResult(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true)));
        using WebSocketMessageStream stream = new(socket, LimboLogs.Instance);

        ReceiveResult result = await stream.ReceiveAsync(new byte[16], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Closed, Is.EqualTo(receives));
            _ = socket.Received(receives ? 1 : 0).ReceiveAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task Text_only_streams_reject_binary_messages_without_changing_the_default(
        [Values] bool textOnly, [Values(WebSocketMessageType.Text, WebSocketMessageType.Binary)] WebSocketMessageType messageType)
    {
        WebSocketMock socket = new(new Queue<WebSocketReceiveResult>([new(1, messageType, true)])) { Payload = [0x7b] };
        using WebSocketMessageStream stream = textOnly
            ? new(socket, NullLogManager.Instance, textOnly: true)
            : new(socket, NullLogManager.Instance);
        byte[] buffer = new byte[1];

        ReceiveResult received = await stream.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        bool rejected = textOnly && messageType == WebSocketMessageType.Binary;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received.Closed, Is.EqualTo(rejected));
            Assert.That(received.Read, Is.EqualTo(rejected ? 0 : 1));
            Assert.That(received.EndOfMessage, Is.EqualTo(!rejected));
        }
    }

    [Test]
    public void Correct_isnull_on_result()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ReceiveResult().IsNull, Is.False);
            Assert.That(default(ReceiveResult).IsNull, Is.True);
        }
    }
}
