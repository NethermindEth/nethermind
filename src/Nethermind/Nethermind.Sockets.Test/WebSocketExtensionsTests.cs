// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NSubstitute.Extensions;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Sockets.Test
{
    public class WebSocketExtensionsTests
    {
        private class WebSocketMock : WebSocket
        {
            private readonly Queue<WebSocketReceiveResult> _receiveResults;

            public WebSocketMock(Queue<WebSocketReceiveResult> receiveResults)
            {
                _receiveResults = receiveResults;
            }

            public override void Abort()
            {
                throw new NotImplementedException();
            }

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public override void Dispose()
            {
                throw new NotImplementedException();
            }

            private byte byteIndex = 0;

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                for (int i = 0; i < buffer.Count; i++)
                {
                    unchecked
                    {
                        buffer[i] = byteIndex++;
                    }
                }

                if (!_receiveResults.Any() && ReturnTaskWithFaultOnEmptyQueue)
                {
                    Task<WebSocketReceiveResult> a = new Task<WebSocketReceiveResult>(() => throw new Exception());
                    a.Start();
                    return a;
                }

                return Task.FromResult(_receiveResults.Dequeue());
            }

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
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
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            receiveResult.Enqueue(new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false));
            receiveResult.Enqueue(new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false));
            receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            WebSocketMock mock = new(receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received().ProcessAsync(Arg.Is<ArraySegment<byte>>(ba => ba.Count == 2 * 4096 + 1024));
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
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            WebSocketMock mock = new(receiveResult);

            var processor = Substitute.For<IJsonRpcProcessor>();
            processor.ProcessAsync(default, default).ReturnsForAnyArgs((x) => new List<JsonRpcResult>()
            {
                (JsonRpcResult.Single((new JsonRpcResponse()), new RpcReport())),
                (JsonRpcResult.Collection(new JsonRpcBatchResult((e, c) =>
                    new List<JsonRpcResult.Entry>()
                {
                    new(new JsonRpcResponse(), new RpcReport()),
                    new(new JsonRpcResponse(), new RpcReport()),
                    new(new JsonRpcResponse(), new RpcReport()),
                }.ToAsyncEnumerable().GetAsyncEnumerator(c))))
            }.ToAsyncEnumerable());

            var service = Substitute.For<IJsonRpcService>();

            var localStats = Substitute.For<IJsonRpcLocalStats>();

            var webSocketsClient = Substitute.ForPartsOf<JsonRpcSocketsClient>(
                "TestClient",
                new WebSocketHandler(mock, Substitute.For<ILogManager>()),
                RpcEndpoint.Ws,
                processor,
                service,
                localStats,
                Substitute.For<IJsonSerializer>(),
                null,
                30.MB());

            webSocketsClient.Configure().SendJsonRpcResult(default).ReturnsForAnyArgs(async x =>
            {
                var par = x.Arg<JsonRpcResult>();
                return await Task.FromResult(par.IsCollection ? par.BatchedResponses.ToListAsync().Result.Count * 100 : 100);
            });

            await webSocketsClient.ReceiveAsync();

            Assert.That(Metrics.JsonRpcBytesReceivedWebSockets, Is.EqualTo(1024));
            Assert.That(Metrics.JsonRpcBytesSentWebSockets, Is.EqualTo(400));
            localStats.Received(1).ReportCall(Arg.Any<RpcReport>(), Arg.Any<long>(), 100);
            localStats.Received(1).ReportCall(Arg.Any<RpcReport>(), Arg.Any<long>(), 300);
        }

        [Test]
        public async Task Can_receive_many_messages()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            for (int i = 0; i < 1000; i++)
            {
                receiveResult.Enqueue(new WebSocketReceiveResult(1234, WebSocketMessageType.Text, true));
            }

            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

            WebSocketMock mock = new(receiveResult);
            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received(1000).ProcessAsync(Arg.Is<ArraySegment<byte>>(ba => ba.Count == 1234));
        }

        [Test]
        public async Task Can_receive_whole_message_non_buffer_sizes()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            for (int i = 0; i < 6; i++)
            {
                receiveResult.Enqueue(new WebSocketReceiveResult(2000, WebSocketMessageType.Text, false));
            }

            receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            WebSocketMock mock = new(receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received().ProcessAsync(Arg.Is<ArraySegment<byte>>(ba => ba.Count == 6 * 2000 + 1));
        }

        [Test]
        public async Task Throws_on_too_long_message()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            for (int i = 0; i < 1024; i++)
            {
                receiveResult.Enqueue(new WebSocketReceiveResult(5 * 1024, WebSocketMessageType.Text, false));
            }

            receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            WebSocketMock mock = new(receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            Assert.ThrowsAsync<InvalidOperationException>(async () => await webSocketsClient.ReceiveAsync());
            await webSocketsClient.DidNotReceive().ProcessAsync(Arg.Any<ArraySegment<byte>>());
        }

        [Test, Timeout(5000)]
        public async Task Stops_on_dirty_disconnect()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
            WebSocketMock mock = new(receiveResult);
            mock.ReturnTaskWithFaultOnEmptyQueue = true;

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
        }
    }
}
