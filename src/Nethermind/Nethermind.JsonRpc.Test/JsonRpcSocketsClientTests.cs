// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class JsonRpcSocketsClientTests
{
    public class UsingIpc
    {
        [Test]
        [Explicit("Takes too long to run")]
        public async Task Can_handle_very_large_objects()
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveBytes = OneShotServer(
                ipEndPoint,
                CountNumberOfBytes
            );

            JsonRpcSuccessResponse bigObject = RandomSuccessResponse(200_000);
            Task<int> sendJsonRpcResult = Task.Run(async () =>
            {
                using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                IpcSocketMessageStream handler = new(socket);
                JsonRpcSocketsClient<IpcSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(bigObject, default);

                return await client.SendJsonRpcResult(result);
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

            Task<int> receiveBytes = OneShotServer(
                ipEndPoint,
                CountNumberOfBytes
            );

            Task<int> sendCollection = Task.Run(async () =>
            {
                using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                IpcSocketMessageStream handler = new(socket);
                JsonRpcSocketsClient<IpcSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: handler,
                    endpointType: RpcEndpoint.IPC,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));

                return await client.SendJsonRpcResult(result);
            });

            await Task.WhenAll(sendCollection, receiveBytes);
            int sent = sendCollection.Result;
            int received = receiveBytes.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [Test]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            CancellationTokenSource cts = new();
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveMessages = OneShotServer(
                ipEndPoint,
                async socket => await CountNumberOfMessages(socket, cts.Token)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                using IpcSocketMessageStream stream = new(socket);
                using JsonRpcSocketsClient<IpcSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: stream,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(RandomSuccessResponse(1_000), default);

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

            Assert.That(received, Is.EqualTo(sent));
        }

        [Test]
        public async Task Fuzz_messages_integrity()
        {
            static async Task<int> ReadMessages(Socket socket, IList<byte[]> receivedMessages, CancellationToken token)
            {
                using IpcSocketMessageStream stream = new(socket);

                int messages = 0;
                byte[] msg = [];
                try
                {
                    byte[] buffer = new byte[10];
                    while (true)
                    {
                        ReceiveResult? result = await stream.ReceiveAsync(buffer);
                        if (result is not null)
                            msg = msg.Concat(buffer.Take(result.Read)).ToArray();
                        if (result?.EndOfMessage == true)
                        {
                            messages++;
                            receivedMessages.Add(msg);
                            msg = [];
                        }

                        if (result is null || result.Closed)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                return messages;
            }

            CancellationTokenSource cts = new();
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            List<byte[]> sentMessages = [];
            List<byte[]> receivedMessages = [];

            Task<int> receiveMessages = OneShotServer(
                ipEndPoint,
                async socket => await ReadMessages(socket, receivedMessages, cts.Token)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                int messageCount = 0;
                using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                using IpcSocketMessageStream stream = new(socket);
                using JsonRpcSocketsClient<IpcSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: stream,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(RandomSuccessResponse(1_000), default);


                for (int i = 1; i < 244; i++)
                {
                    messageCount++;
                    var msg = Enumerable.Range(11, i).Select(x => (byte)x).ToArray();
                    sentMessages.Add(msg);
                    await stream.WriteAsync(msg);
                    await stream.WriteEndOfMessageAsync();
                    if (i % 10 == 0)
                    {
                        await Task.Delay(100);
                    }
                }
                stream.Close();
                cts.Cancel();

                return messageCount;
            });

            await Task.WhenAll(sendMessages, receiveMessages);
            int sent = sendMessages.Result;
            int received = receiveMessages.Result;
            Assert.That(received, Is.EqualTo(sent));
            CollectionAssert.AreEqual(sentMessages, receivedMessages);
        }

        private static async Task<int> CountNumberOfMessages(Socket socket, CancellationToken token)
        {
            using IpcSocketMessageStream stream = new(socket);

            int messages = 0;
            try
            {
                byte[] buffer = new byte[10];
                while (true)
                {
                    ReceiveResult? result = await stream.ReceiveAsync(buffer);
                    if (result?.EndOfMessage == true)
                    {
                        messages++;
                    }

                    if (result is null || result.Closed)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }

            return messages;
        }

        private static async Task<T> OneShotServer<T>(IPEndPoint ipEndPoint, Func<Socket, Task<T>> func)
        {
            using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
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
    }

    [Explicit]
    public class UsingWebSockets
    {
        [Test]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            CancellationTokenSource cts = new();

            Task<int> receiveMessages = OneShotServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfMessages(webSocket, cts.Token)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using WebSocketMessageStream handler = new(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Single(RandomSuccessResponse(1_000), default);

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

            Task<int> server = OneShotServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfMessages(webSocket, cts.Token)
            );

            Task sendCollection = Task.Run(async () =>
            {
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using WebSocketMessageStream handler = new(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer()
                );
                JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));

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

            Task<long> receiveBytes = OneShotServer(
                "http://localhost:1337/",
                async webSocket => await CountNumberOfBytes(webSocket, cts.Token)
            );

            Task<int> sendCollection = Task.Run(async () =>
            {
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);

                using WebSocketMessageStream handler = new(socket, NullLogManager.Instance);
                using JsonRpcSocketsClient<WebSocketMessageStream> client = new(
                    clientName: "TestClient",
                    stream: handler,
                    endpointType: RpcEndpoint.Ws,
                    jsonRpcProcessor: null!,
                    jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                    jsonSerializer: new EthereumJsonSerializer(),
                    maxBatchResponseBodySize: maxByteCount
                );
                JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));

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

        private static async Task<T> OneShotServer<T>(string uri, Func<WebSocket, Task<T>> func)
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
    }

    private static JsonRpcBatchResult RandomBatchResult(int items, int size)
    {
        return new JsonRpcBatchResult((_, token) =>
            RandomAsyncEnumerable(
                items,
                () => Task.FromResult(new JsonRpcResult.Entry(RandomSuccessResponse(size), default))
            ).GetAsyncEnumerator(token)
        );
    }

    private static JsonRpcSuccessResponse RandomSuccessResponse(int size)
    {
        return new JsonRpcSuccessResponse
        {
            MethodName = "mock",
            Id = "42",
            Result = RandomObject(size)
        };
    }

    private static async IAsyncEnumerable<T> RandomAsyncEnumerable<T>(int items, Func<Task<T>> factory)
    {
        for (int i = 0; i < items; i++)
        {
            T value = await factory();
            yield return value;
        }
    }

    private static object RandomObject(int size)
    {
        string[] strings = RandomStringArray(size / 2);
        object obj = new GethLikeTxTrace()
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = strings, Memory = strings,
                }
            }
        };
        return obj;
    }

    private static string[] RandomStringArray(int length, bool runGc = true)
    {
        string[] array = new string[length];
        for (int i = 0; i < length; i++)
        {
            array[i] = RandomString(length);
            if (runGc && i % 100 == 0)
            {
                GC.Collect();
            }
        }
        return array;
    }

    private static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        char[] stringChars = new char[length];
        Random random = new();

        for (int i = 0; i < stringChars.Length; i++)
        {
            stringChars[i] = chars[random.Next(chars.Length)];
        }
        return new string(stringChars);
    }
}
