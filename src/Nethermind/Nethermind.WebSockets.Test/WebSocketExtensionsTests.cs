using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.WebSockets.Test
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
            WebSocketMock mock = new WebSocketMock(receiveResult);

            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();

            await mock.ReceiveAsync(webSocketsClient);
            await webSocketsClient.Received().ReceiveAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 2 * 4096 + 1024));
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

            WebSocketMock mock = new WebSocketMock(receiveResult);
            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();

            await mock.ReceiveAsync(webSocketsClient);
            await webSocketsClient.Received(1000).ReceiveAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 1234));
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
            WebSocketMock mock = new WebSocketMock(receiveResult);

            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();

            await mock.ReceiveAsync(webSocketsClient);
            await webSocketsClient.Received().ReceiveAsync(Arg.Is<Memory<byte>>(ba => ba.Length == 6 * 2000 + 1));
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
            WebSocketMock mock = new WebSocketMock(receiveResult);

            IWebSocketsClient webSocketsClient = Substitute.For<IWebSocketsClient>();
            
            Assert.ThrowsAsync<InvalidOperationException>(async () => await mock.ReceiveAsync(webSocketsClient));
            await webSocketsClient.DidNotReceive().ReceiveAsync(Arg.Any<Memory<byte>>());
        }
    }
}