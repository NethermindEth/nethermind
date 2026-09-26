// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
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
            // Had to use Array.Fill as it is more performant
            Array.Fill(buffer.Array, (byte)0, buffer.Offset, buffer.Count);

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
        public override WebSocketState State { get; } = WebSocketState.Open;
        public override string SubProtocol { get; }
        public bool ReturnTaskWithFaultOnEmptyQueue { get; set; }
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
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);

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
                Arg.Any<PipeReader>(),
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
            1);

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
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);
        await webSocketsClient.Received(1000).ProcessAsync(Arg.Is<ArraySegment<byte>>(static ba => ba.Count == 1234), cts.Token);
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
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);


        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await webSocketsClient.ReceiveLoopAsync(cts.Token);
        await webSocketsClient.Received().ProcessAsync(Arg.Is<ArraySegment<byte>>(static ba => ba.Count == 6 * 2000 + 1), cts.Token);
    }

    [Test]
    public async Task Throws_on_too_long_message()
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
            (int)1.MB);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await webSocketsClient.ReceiveLoopAsync(cts.Token));
        await webSocketsClient.DidNotReceive().ProcessAsync(Arg.Any<ArraySegment<byte>>(), cts.Token);
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
            SocketClient<WebSocketMessageStream>.MAX_REQUEST_BODY_SIZE_FOR_ENGINE_API);

        await webSocketsClient.ReceiveLoopAsync(cts.Token);
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
