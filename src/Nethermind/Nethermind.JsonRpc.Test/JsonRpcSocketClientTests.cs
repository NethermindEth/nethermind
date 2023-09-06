// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Test.Sockets;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class JsonRpcSocketClientTests
{
    private static readonly object _bigObject = new RandomObject(100_000);

    class UsingIpc
    {
        private static async Task<T> MockServer<T>(IPEndPoint ipEndPoint, Func<Socket, Task<T>> func)
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(ipEndPoint);
            socket.Listen();

            Socket handler = await socket.AcceptAsync();

            return await func(handler);
        }

        private static async Task<int> CountNumberOfBytes(Socket socket)
        {
            byte[] buffer = new byte[1024];
            int totalRead = 0;

            int read;
            while ((read = await socket.ReceiveAsync(buffer)) != 0)
            {
                totalRead += read;
            }
            return totalRead;
        }

        [Test]
        [Explicit("Takes too long to run")]
        public async Task Can_handle_very_large_objects()
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveBytes = MockServer(
                ipEndPoint,
                async socket => await CountNumberOfBytes(socket)
            );

            Task<int> sendJsonRpcResult = Task.Run(async () =>
            {
                using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                ISocketHandler handler = new IpcSocketsHandler(socket);
                JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse()
                    {
                        MethodName = "mock", Id = "42", Result = _bigObject
                    }, default);

                return await client.SendJsonRpcResult(result);
            });

            await Task.WhenAll(sendJsonRpcResult, receiveBytes);
            int sent = sendJsonRpcResult.Result;
            int received = receiveBytes.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [Test]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveBytes = MockServer(
                ipEndPoint,
                async socket => await CountNumberOfBytes(socket)
            );

            Task<int> sendJsonRpcResult = Task.Run(async () =>
            {
                using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                ISocketHandler handler = new IpcSocketsHandler(socket);
                JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse()
                    {
                        MethodName = "mock", Id = "42", Result = new RandomObject(1000).Get()
                    }, default);

                int totalBytesSent = 0;
                for (int i = 0; i < messageCount; i++)
                {
                    totalBytesSent += await client.SendJsonRpcResult(result);
                }

                return totalBytesSent;
            });

            await Task.WhenAll(sendJsonRpcResult, receiveBytes);
            int sent = sendJsonRpcResult.Result;
            int received = receiveBytes.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_collections(int elements)
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveBytes = MockServer(
                ipEndPoint,
                async socket => await CountNumberOfBytes(socket)
            );

            Task<int> sendCollection = Task.Run(async () =>
            {
                using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                ISocketHandler handler = new IpcSocketsHandler(socket);
                JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
                );
                JsonRpcResult result = JsonRpcResult.Collection(
                    new JsonRpcBatchResult((_, token) =>
                        new RandomAsyncEnumerable<JsonRpcResult.Entry>(
                            10,
                            () => Task.FromResult(
                                new JsonRpcResult.Entry(
                                    new JsonRpcSuccessResponse
                                    {
                                        MethodName = "mock", Id = "42", Result = new RandomObject(100),
                                    }, default
                                )
                            )
                        ).GetAsyncEnumerator(token)
                    )
                );

                return await client.SendJsonRpcResult(result);
            });

            await Task.WhenAll(sendCollection, receiveBytes);
            int sent = sendCollection.Result;
            int received = receiveBytes.Result;
            Assert.That(sent, Is.EqualTo(received));
        }
    }

    class UsingWebSockets
    {
        private static async Task<T> MockServer<T>(string uri, Func<WebSocket, Task<T>> func)
        {
            using HttpListener httpListener = new();
            httpListener.Prefixes.Add(uri);
            httpListener.Start();

            HttpListenerContext context = await httpListener.GetContextAsync();
            HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
            return await func(webSocketContext.WebSocket);
        }

        private static async Task<int> CountNumberOfMessages(WebSocket webSocket, CancellationToken token)
        {
            int messages = 0;
            try
            {
                byte[] buffer = new byte[1024];

                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.EndOfMessage)
                    {
                        messages++;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
                }
                webSocket.Dispose();
            }

            return messages;
        }

        private static async Task<long> CountNumberOfBytes(WebSocket webSocket, CancellationToken token)
        {
            long bytes = 0;
            try
            {
                byte[] buffer = new byte[1024];

                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    bytes += result.Count;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
                }
                webSocket.Dispose();
            }

            return bytes;
        }

        [Test]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            CancellationTokenSource cts = new();

            Task<int> receiveMessages = MockServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfMessages(webSocket, cts.Token)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                using ClientWebSocket socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using ISocketHandler handler = new WebSocketHandler(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(
                    new JsonRpcSuccessResponse
                    {
                        MethodName = "mock", Id = "42", Result = new RandomObject(1000).Get()
                    },
                    default);

                for (int i = 0; i < messageCount; i++)
                {
                    await client.SendJsonRpcResult(result);
                    await Task.Delay(100);
                }
                cts.Cancel();

                return messageCount;
            });

            await Task.WhenAll(sendMessages, receiveMessages);
            int sent = sendMessages.Result;
            int received = receiveMessages.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_collections(int elements)
        {
            CancellationTokenSource cts = new();

            Task<int> server = MockServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfMessages(webSocket, cts.Token)
            );

            Task sendCollection = Task.Run(async () =>
            {
                using ClientWebSocket socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using ISocketHandler handler = new WebSocketHandler(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Collection(
                    new JsonRpcBatchResult((_, token) =>
                        new RandomAsyncEnumerable<JsonRpcResult.Entry>(
                            10,
                            () => Task.FromResult(
                                new JsonRpcResult.Entry(
                                    new JsonRpcSuccessResponse
                                    {
                                        MethodName = "mock", Id = "42", Result = new RandomObject(100),
                                    }, default
                                )
                            )
                        ).GetAsyncEnumerator(token)
                    )
                );

                await client.SendJsonRpcResult(result);

                await Task.Delay(100);
                cts.Cancel();
            });

            await Task.WhenAll(sendCollection, server);
            Assert.That(server.Result, Is.EqualTo(1));
        }

        [TestCase(1_000)]
        [TestCase(5_000)]
        [TestCase(10_000)]
        [Ignore("Feature does not work correctly")]
        public async Task Stops_on_limited_body_size(int maxByteCount)
        {
            CancellationTokenSource cts = new();

            Task<long> receiveBytes = MockServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfBytes(webSocket, cts.Token)
            );

            Task<int> sendCollection = Task.Run(async () =>
            {
                using ClientWebSocket socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using ISocketHandler handler = new WebSocketHandler(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                    clientName: "TestClient",
                    handler: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcService: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer(),
                    maxBatchResponseBodySize: maxByteCount
                );
                JsonRpcResult result = JsonRpcResult.Collection(
                    new JsonRpcBatchResult((_, token) =>
                        new RandomAsyncEnumerable<JsonRpcResult.Entry>(
                            10,
                            () => Task.FromResult(
                                new JsonRpcResult.Entry(
                                    new JsonRpcSuccessResponse
                                    {
                                        MethodName = "mock", Id = "42", Result = new RandomObject(100),
                                    }, default
                                )
                            )
                        ).GetAsyncEnumerator(token)
                    )
                );

                int sent = await client.SendJsonRpcResult(result);

                await Task.Delay(100);
                cts.Cancel();

                return sent;
            });

            await Task.WhenAll(sendCollection, receiveBytes);
            int sent = sendCollection.Result;
            long received = receiveBytes.Result;
            Assert.That(received, Is.LessThanOrEqualTo(Math.Min(sent, maxByteCount)));
        }
    }

}
