// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.IO;
using Nethermind.Blockchain.Tracing.GethStyle;
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
[Parallelizable(ParallelScope.Children)]
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
                using TestClient<IpcSocketMessageStream> ipc = await TestClient.ConnectTcpAsync(ipEndPoint);
                using JsonRpcResult result = JsonRpcResult.Single(bigObject, default);

                return await ipc.Client.SendJsonRpcResult(result);
            });

            await Task.WhenAll(sendJsonRpcResult, receiveBytes);
            int sent = sendJsonRpcResult.Result;
            int received = receiveBytes.Result;
            Assert.That(sent, Is.EqualTo(received));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(10)]
        [TestCase(50)]
        public async Task Can_send_multiple_messages(int messageCount)
        {
            static async Task<int> CountNumberOfMessages(Socket socket)
            {
                await using IpcSocketMessageStream stream = new(socket);

                int messages = 0;
                try
                {
                    byte[] buffer = new byte[10];
                    while (true)
                    {
                        ReceiveResult result = await stream.ReceiveAsync(buffer);

                        // Imitate random delays
                        if (Stopwatch.GetTimestamp() % 101 == 0)
                            await Task.Delay(1);

                        if (!result.IsNull && IsEndOfIpcMessage(result))
                        {
                            messages++;
                        }

                        if (result.IsNull || result.Closed)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                return messages;
            }

            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveMessages = OneShotServer(
                ipEndPoint,
                CountNumberOfMessages
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                using TestClient<IpcSocketMessageStream> ipc = await TestClient.ConnectTcpAsync(ipEndPoint);
                int disposeCount = 0;

                for (int i = 0; i < messageCount; i++)
                {
                    using JsonRpcResult result = JsonRpcResult.Single(RandomSuccessResponse(100, () => disposeCount++), default);
                    await ipc.Client.SendJsonRpcResult(result);
                    await Task.Delay(1);
                }

                disposeCount.Should().Be(messageCount);

                return messageCount;
            });

            await Task.WhenAll(sendMessages, receiveMessages);
            int sent = sendMessages.Result;
            int received = receiveMessages.Result;

            Assert.That(received, Is.EqualTo(sent));
        }

        [TestCase(1)]
        [TestCase(5)]
        public async Task CanHandleMessageConcurrently(int concurrencyLevel)
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();

            IJsonRpcProcessor jsonRpcProcessor = Substitute.For<IJsonRpcProcessor>();
            Task receiver = StartReceiver(pair.Listener, jsonRpcProcessor, pair.Cts.Token, concurrencyLevel);

            await using IpcSocketMessageStream sendStream = new(pair.SendSocket);

            int concurrentCall = 0;
            TaskCompletionSource completeSource = new();
            async IAsyncEnumerable<JsonRpcResult> ResponseFunc(CallInfo c)
            {
                Interlocked.Increment(ref concurrentCall);
                await completeSource.Task;
                yield return JsonRpcResult.Single(new JsonRpcSuccessResponse(null), new RpcReport());
            }

            jsonRpcProcessor
                .ProcessAsync(Arg.Any<PipeReader>(), Arg.Any<JsonRpcContext>())
                .Returns(ResponseFunc);

            for (int i = 0; i < concurrencyLevel; i++)
            {
                await sendStream.WriteAsync("test"u8.ToArray(), pair.Cts.Token);
                await sendStream.WriteEndOfMessageAsync();
            }

            Assert.That(() => concurrentCall, Is.EqualTo(concurrencyLevel).After(10000, 10));
            completeSource.SetResult();

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        [Test]
        public async Task Does_not_process_partial_message_without_delimiter()
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();

            int processedRequests = 0;
            int processedRequestSize = 0;
            IJsonRpcProcessor jsonRpcProcessor = CreateCapturingProcessor(buf =>
            {
                processedRequestSize = (int)buf.Length;
                Interlocked.Increment(ref processedRequests);
            });

            Task receiver = StartReceiver(pair.Listener, jsonRpcProcessor, pair.Cts.Token);

            await using IpcSocketMessageStream sendStream = new(pair.SendSocket);
            string requestPayload = new('a', 300_000);
            string request = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_newPayloadV4\",\"params\":[\"{requestPayload}\"]}}";
            byte[] requestBytes = Encoding.UTF8.GetBytes(request);
            byte[] firstChunk = [requestBytes[0]];

            await sendStream.WriteAsync(firstChunk, pair.Cts.Token);
            await Task.Delay(100, pair.Cts.Token);

            processedRequests.Should().Be(0);

            await sendStream.WriteAsync(requestBytes.AsMemory(1), pair.Cts.Token);
            await sendStream.WriteEndOfMessageAsync();

            Assert.That(() => processedRequests, Is.EqualTo(1).After(5000, 10));
            processedRequestSize.Should().Be(requestBytes.Length);

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task Can_process_complete_messages_without_delimiter(int messageCount)
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();

            int processedRequests = 0;
            List<string> processedPayloads = [];
            IJsonRpcProcessor jsonRpcProcessor = CreateCapturingProcessor(buf =>
            {
                processedPayloads.Add(Encoding.UTF8.GetString(buf.ToArray()));
                Interlocked.Increment(ref processedRequests);
            });

            Task receiver = StartReceiver(pair.Listener, jsonRpcProcessor, pair.Cts.Token);

            await using IpcSocketMessageStream sendStream = new(pair.SendSocket);
            List<string> expectedPayloads = [];
            string[] requests =
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_blockNumber\",\"params\":[]}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"eth_chainId\",\"params\":[]}"
            };

            for (int i = 0; i < messageCount; i++)
            {
                expectedPayloads.Add(requests[i]);
            }
            byte[] combinedRequests = Encoding.UTF8.GetBytes(string.Concat(expectedPayloads));

            await sendStream.WriteAsync(combinedRequests, pair.Cts.Token);

            Assert.That(() => processedRequests, Is.EqualTo(messageCount).After(5000, 10));
            processedPayloads.Should().Equal(expectedPayloads);

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        [Test]
        public async Task Json_parse_state_resets_between_consecutive_messages()
        {
            // Both messages >4KB to span multiple SocketClient buffer reads, triggering incremental JSON state
            string payload1 = new('x', 5000);
            string payload2 = new('y', 5000);
            string request1 = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_call\",\"params\":[\"{payload1}\"]}}";
            string request2 = $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"eth_call\",\"params\":[\"{payload2}\"]}}";

            await SendAndAssertPayloads(request1 + request2, [request1, request2]);
        }

        [TestCase(5)]
        [TestCase(10)]
        public async Task Can_process_large_chunked_json_without_delimiter(int messageCount)
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();

            int processedRequests = 0;
            List<string> processedPayloads = [];
            IJsonRpcProcessor jsonRpcProcessor = CreateCapturingProcessor(buf =>
            {
                processedPayloads.Add(Encoding.UTF8.GetString(buf.ToArray()));
                Interlocked.Increment(ref processedRequests);
            });

            Task receiver = StartReceiver(pair.Listener, jsonRpcProcessor, pair.Cts.Token);

            await using IpcSocketMessageStream sendStream = new(pair.SendSocket);

            // Build large JSON messages (~10KB each) to exercise incremental parsing across many 4KB chunks
            List<string> expectedPayloads = [];
            for (int i = 0; i < messageCount; i++)
            {
                string payload = new((char)('a' + i % 26), 10_000);
                string request = $"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"eth_call\",\"params\":[\"{payload}\"]}}";
                expectedPayloads.Add(request);

                // Send each message in small pieces to force multi-chunk parsing
                byte[] requestBytes = Encoding.UTF8.GetBytes(request);
                int chunkSize = 4096;
                for (int offset = 0; offset < requestBytes.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, requestBytes.Length - offset);
                    await sendStream.WriteAsync(requestBytes.AsMemory(offset, len), pair.Cts.Token);
                    await Task.Delay(1);
                }
            }

            Assert.That(() => processedRequests, Is.EqualTo(messageCount).After(10000, 10));
            processedPayloads.Should().Equal(expectedPayloads);

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        [Test]
        public async Task Overflow_after_buffer_shrink_preserves_data_order()
        {
            // Message 1: large enough (>8KB) so SocketClient grows the buffer, then shrinks it
            // back to 4KB after processing. The tail after msg1 becomes overflow > 4KB.
            string large = new('L', 9000);
            string msg1 = $"{{\"id\":1,\"params\":[\"{large}\"]}}";

            // Messages 2+3: combined overflow > 4KB, sent without delimiters right after msg1.
            // They end up in the overflow. After the buffer shrinks to 4KB, partial CopyTo
            // drains part of the overflow. If a boundary is found, the tail must be
            // correctly ordered before the undrained remainder.
            string large2 = new('M', 5000);
            string msg2 = $"{{\"id\":2,\"method\":\"eth_call\",\"params\":[\"{large2}\"]}}";
            string msg3 = "{\"id\":3,\"method\":\"eth_call\",\"params\":[]}";

            await SendAndAssertPayloads(msg1 + "\n" + msg2 + msg3, [msg1, msg2, msg3], timeout: 10000);
        }

        private static IEnumerable<TestCaseData> JsonBoundaryDetectionCases()
        {
            string json1 = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_blockNumber\",\"params\":[]}";
            string json2 = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"eth_chainId\",\"params\":[]}";

            // JSON without trailing \n followed by newline-delimited message
            yield return new TestCaseData(
                json1 + json2 + "\n",
                new[] { json1, json2 }
            ).SetName("Json_without_newline_followed_by_newline_delimited_message");

            // JSON with escaped \n (backslash-n) in string value — 0x0A never appears on the wire, not a delimiter
            string jsonWithEscapedNewline = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"line1\\nline2\"}";
            yield return new TestCaseData(
                jsonWithEscapedNewline,
                new[] { jsonWithEscapedNewline }
            ).SetName("Json_with_escaped_newline_in_string_value");

            // Literal 0x0A byte inside a JSON string value (invalid JSON per spec).
            // Utf8JsonReader rejects the raw control character and throws JsonException,
            // causing fallback to the \n-delimiter path which splits at the 0x0A byte.
            string jsonBeforeLiteralNewline = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"line1";
            string jsonAfterLiteralNewline = "line2\"}";
            yield return new TestCaseData(
                jsonBeforeLiteralNewline + "\n" + jsonAfterLiteralNewline,
                new[] { jsonBeforeLiteralNewline }
            ).SetName("Json_with_literal_0x0A_byte_in_string_value");
        }

        [TestCaseSource(nameof(JsonBoundaryDetectionCases))]
        public async Task Json_boundary_detection(string wireData, string[] expectedPayloads) =>
            await SendAndAssertPayloads(wireData, expectedPayloads);

        [TestCase(10)]
        [TestCase(63)]
        [TestCase(1024)]
        [TestCase(1024000)]
        public async Task Fuzz_messages_integrity(int bufferSize)
        {
            async Task<int> ReadMessages(Socket socket, IList<byte[]> receivedMessages)
            {
                await using IpcSocketMessageStream stream = new(socket);

                int messages = 0;
                List<byte> msg = [];
                try
                {
                    byte[] buffer = new byte[bufferSize];
                    while (true)
                    {
                        ReceiveResult result = await stream.ReceiveAsync(buffer);
                        if (!result.IsNull)
                        {
                            for (int j = 0; j < result.Read; j++)
                                msg.Add(buffer[j]);

                            if (IsEndOfIpcMessage(result))
                            {
                                messages++;
                                receivedMessages.Add(msg.ToArray());
                                msg = [];
                            }
                        }

                        if (result.IsNull || result.Closed)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                return messages;
            }

            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            List<byte[]> sentMessages = [];
            List<byte[]> receivedMessages = [];

            Task<int> receiveMessages = OneShotServer(
                ipEndPoint,
                async socket => await ReadMessages(socket, receivedMessages)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                int messageCount = 0;
                using Socket socket = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(ipEndPoint);

                await using IpcSocketMessageStream stream = new(socket);

                for (int i = 1; i < 244; i++)
                {
                    messageCount++;
                    byte[] msgWithDelimiter = new byte[i + 1];
                    for (int j = 0; j < i; j++)
                        msgWithDelimiter[j] = (byte)(11 + j);
                    msgWithDelimiter[i] = (byte)'\n';
                    sentMessages.Add(msgWithDelimiter[..i]);
                    await stream.WriteAsync(msgWithDelimiter);

                    if (i % 10 == 0)
                    {
                        await Task.Delay(1);
                    }
                }
                stream.Close();

                return messageCount;
            });

            await Task.WhenAll(sendMessages, receiveMessages);
            int sent = sendMessages.Result;
            int received = receiveMessages.Result;

            Assert.That(received, Is.EqualTo(sent));
            Assert.That(sentMessages, Is.EqualTo(receivedMessages).AsCollection);
        }

        private static async Task SendAndAssertPayloads(string wireData, IReadOnlyList<string> expectedPayloads, int timeout = 5000)
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();

            int processedRequests = 0;
            List<string> processedPayloads = [];
            IJsonRpcProcessor jsonRpcProcessor = CreateCapturingProcessor(buf =>
            {
                processedPayloads.Add(Encoding.UTF8.GetString(buf.ToArray()));
                Interlocked.Increment(ref processedRequests);
            });

            Task receiver = StartReceiver(pair.Listener, jsonRpcProcessor, pair.Cts.Token);

            await using IpcSocketMessageStream sendStream = new(pair.SendSocket);
            await sendStream.WriteAsync(Encoding.UTF8.GetBytes(wireData), pair.Cts.Token);

            Assert.That(() => processedRequests, Is.EqualTo(expectedPayloads.Count).After(timeout, 10));
            processedPayloads.Should().Equal(expectedPayloads);

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        private static Task StartReceiver(Socket listener, IJsonRpcProcessor processor, CancellationToken token, int concurrency = 1) =>
            Task.Run(async () =>
            {
                Socket socket = await listener.AcceptAsync(token);
                using TestClient<IpcSocketMessageStream> tc = new(new IpcSocketMessageStream(socket), jsonRpcProcessor: processor, concurrency: concurrency);
                await tc.Client.ReceiveLoopAsync(token);
            });

        private static async Task ShutdownAndWait(Socket sendSocket, Task receiver)
        {
            sendSocket.Shutdown(SocketShutdown.Send);
            try
            {
                await receiver;
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }

        private static IJsonRpcProcessor CreateCapturingProcessor(Action<ReadOnlySequence<byte>> onRequest)
        {
            IJsonRpcProcessor processor = Substitute.For<IJsonRpcProcessor>();
            async IAsyncEnumerable<JsonRpcResult> ResponseFunc(CallInfo callInfo)
            {
                PipeReader reader = callInfo.ArgAt<PipeReader>(0);
                ReadResult readResult = await reader.ReadToEndAsync();
                ReadOnlySequence<byte> buffer = readResult.Buffer;
                onRequest(buffer);
                reader.AdvanceTo(buffer.End);
                yield return JsonRpcResult.Single(new JsonRpcSuccessResponse(null), new RpcReport());
            }
            processor.ProcessAsync(Arg.Any<PipeReader>(), Arg.Any<JsonRpcContext>()).Returns(ResponseFunc);
            return processor;
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
                async webSocket => await CountMessages(webSocket, cts.Token)
            );

            Task<int> sendMessages = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync();
                using JsonRpcResult result = JsonRpcResult.Single(RandomSuccessResponse(1_000), default);

                for (int i = 0; i < messageCount; i++)
                {
                    await ws.Client.SendJsonRpcResult(result);
                    await Task.Delay(100);
                }
                await cts.CancelAsync();

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
                async webSocket => await CountMessages(webSocket, cts.Token)
            );

            Task sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync();
                using JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));
                await ws.Client.SendJsonRpcResult(result);
                await Task.Delay(100);
                await cts.CancelAsync();
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
                async webSocket => await CountBytes(webSocket, cts.Token)
            );

            Task<int> sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync(maxBatchResponseBodySize: maxByteCount);
                using JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));
                int sent = await ws.Client.SendJsonRpcResult(result);
                await Task.Delay(100);
                await cts.CancelAsync();
                return sent;
            });

            await Task.WhenAll(sendCollection, receiveBytes);
            int sent = sendCollection.Result;
            long received = receiveBytes.Result;
            Assert.That(received, Is.LessThanOrEqualTo(Math.Min(sent, maxByteCount)));
        }

        [Test]
        public async Task Can_serialize_collection()
        {
            MemoryMessageStream stream = new();
            using TestClient<MemoryMessageStream> tc = new(stream, RpcEndpoint.Ws, maxBatchResponseBodySize: 10_000);
            using JsonRpcResult result = JsonRpcResult.Collection(RandomBatchResult(10, 100));
            await tc.Client.SendJsonRpcResult(result);
            stream.Seek(0, SeekOrigin.Begin);
            JsonRpcSuccessResponse[]? response = new EthereumJsonSerializer().Deserialize<JsonRpcSuccessResponse[]>(stream);
            response.Should().NotContainNulls();
        }

        private static Task<int> CountMessages(WebSocket webSocket, CancellationToken token) =>
            ReceiveWebSocket(webSocket, token, 0, (count, r) => r.EndOfMessage ? count + 1 : count);

        private static Task<long> CountBytes(WebSocket webSocket, CancellationToken token) =>
            ReceiveWebSocket(webSocket, token, 0L, (total, r) => total + r.Count);

        private static async Task<T> ReceiveWebSocket<T>(WebSocket webSocket, CancellationToken token, T initial, Func<T, WebSocketReceiveResult, T> accumulate)
        {
            T value = initial;
            try
            {
                byte[] buffer = new byte[1024];
                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    value = accumulate(value, result);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (webSocket.State == WebSocketState.Open)
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
                webSocket.Dispose();
            }
            return value;
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
    }

    private static JsonRpcBatchResult RandomBatchResult(int items, int size) => new((_, token) =>
        RandomAsyncEnumerable(
            items,
            () => Task.FromResult(new JsonRpcResult.Entry(RandomSuccessResponse(size), default))
        ).GetAsyncEnumerator(token)
    );

    private static JsonRpcSuccessResponse RandomSuccessResponse(int size, Action? disposeAction = null) =>
        new(disposeAction)
        {
            MethodName = "mock",
            Id = "42",
            Result = RandomObject(size)
        };

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
        return new GethLikeTxTrace
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = strings, Memory = strings,
                }
            }
        };
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

    private static bool IsEndOfIpcMessage(ReceiveResult result) => result.EndOfMessage && (!result.Closed || result.Read != 0);

    private sealed record UnixSocketPair(CancellationTokenSource Cts, TempPath TmpPath, Socket Listener, Socket SendSocket) : IDisposable
    {
        public static async Task<UnixSocketPair> CreateAsync()
        {
            CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
            TempPath tmpPath = TempPath.GetTempFile();
            UnixDomainSocketEndPoint endPoint = new(tmpPath.Path);
            Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endPoint);
            listener.Listen(0);
            Socket sendSocket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sendSocket.ConnectAsync(endPoint);
            return new UnixSocketPair(cts, tmpPath, listener, sendSocket);
        }

        public void Dispose()
        {
            SendSocket.Dispose();
            Listener.Dispose();
            TmpPath.Dispose();
            Cts.Dispose();
        }
    }

    private sealed class TestClient<TStream>(
        TStream stream,
        RpcEndpoint endpointType = RpcEndpoint.IPC,
        IJsonRpcProcessor? jsonRpcProcessor = null,
        long? maxBatchResponseBodySize = null,
        int concurrency = 1,
        IDisposable? owner = null)
        : IDisposable
        where TStream : Stream, IMessageBorderPreservingStream
    {
        public JsonRpcSocketsClient<TStream> Client { get; } = new(
            clientName: "TestClient",
            stream: stream,
            endpointType: endpointType,
            jsonRpcProcessor: jsonRpcProcessor!,
            jsonRpcLocalStats: new NullJsonRpcLocalStats(),
            jsonSerializer: new EthereumJsonSerializer(),
            maxBatchResponseBodySize: maxBatchResponseBodySize,
            concurrency: concurrency
        );

        public void Dispose()
        {
            Client.Dispose();
            owner?.Dispose();
        }
    }

    private static class TestClient
    {
        public static async Task<TestClient<IpcSocketMessageStream>> ConnectTcpAsync(IPEndPoint endPoint)
        {
            Socket socket = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endPoint);
            IpcSocketMessageStream stream = new(socket);
            return new TestClient<IpcSocketMessageStream>(stream, owner: socket);
        }

        public static async Task<TestClient<WebSocketMessageStream>> ConnectWsAsync(long? maxBatchResponseBodySize = null)
        {
            ClientWebSocket socket = new();
            await socket.ConnectAsync(new Uri("ws://localhost:1337/"), CancellationToken.None);
            WebSocketMessageStream stream = new(socket, NullLogManager.Instance);
            return new TestClient<WebSocketMessageStream>(stream, RpcEndpoint.Ws, maxBatchResponseBodySize: maxBatchResponseBodySize, owner: socket);
        }
    }
}
