using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
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
            public override WebSocketState State { get; }
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
            WebSocketMock mock = new (receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received().ProcessAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 2 * 4096 + 1024));
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

            WebSocketMock mock = new (receiveResult);
            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received(1000).ProcessAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 1234));
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
            WebSocketMock mock = new (receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
            await webSocketsClient.Received().ProcessAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 6 * 2000 + 1));
        }

        [Test]
        public async Task Throws_on_too_long_message()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            for (int i = 0; i < 1024; i++)
            {
                receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, false));
            }

            receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
            receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            WebSocketMock mock = new (receiveResult);

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            Assert.ThrowsAsync<InvalidOperationException>(async () => await webSocketsClient.ReceiveAsync());
            await webSocketsClient.DidNotReceive().ProcessAsync(Arg.Any<Memory<byte>>());
        }
        
        [Test, Timeout(5000)]
        public async Task Stops_on_dirty_disconnect()
        {
            Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
            receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
            WebSocketMock mock = new (receiveResult);
            mock.ReturnTaskWithFaultOnEmptyQueue = true;

            SocketClient webSocketsClient = Substitute.ForPartsOf<SocketClient>("TestClient", new WebSocketHandler(mock, Substitute.For<ILogManager>()), Substitute.For<IJsonSerializer>());

            await webSocketsClient.ReceiveAsync();
        }
    }
}
