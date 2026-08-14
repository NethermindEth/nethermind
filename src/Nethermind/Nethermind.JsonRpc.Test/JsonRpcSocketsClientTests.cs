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
    [TestCase(false, TestName = "Single response")]
    [TestCase(true, TestName = "Batch response")]
    public async Task Socket_sink_writes_response_with_one_message_boundary(bool isBatch)
    {
        using SocketSinkFixture fixture = CreateSocketSink();

        if (isBatch)
        {
            await fixture.Sink.BeginBatchAsync(CancellationToken.None);
            await fixture.Sink.WriteBatchItemAsync(new JsonRpcSuccessResponse { Id = 1, Result = "0x1" }, new RpcReport("eth_blockNumber", 1, true), CancellationToken.None);
            await fixture.Sink.WriteBatchItemAsync(new JsonRpcSuccessResponse { Id = 2, Result = "0x2" }, new RpcReport("eth_chainId", 1, true), CancellationToken.None);
            await fixture.Sink.EndBatchAsync(CancellationToken.None);
        }
        else
        {
            await fixture.Sink.WriteSingleAsync(new JsonRpcSuccessResponse { Id = 1, Result = "0x1" }, new RpcReport("eth_blockNumber", 1, true), CancellationToken.None);
        }

        byte[] response = fixture.Stream.ToArray();
        if (isBatch)
        {
            Assert.That(response[0], Is.EqualTo((byte)'['));
        }

        Assert.That(response.AsSpan().Count((byte)'\n'), Is.EqualTo(1));
        Assert.That(fixture.Sink.BytesWritten, Is.EqualTo(response.Length));
    }

    [TestCase(RpcEndpoint.Ws, true)]
    [TestCase(RpcEndpoint.IPC, false)]
    public async Task Socket_sink_response_limit_stop_depends_on_authentication(RpcEndpoint endpoint, bool expectedStopRequested)
    {
        using SocketSinkFixture fixture = CreateSocketSink(endpoint, maxBatchResponseBodySize: 1);

        await fixture.Sink.BeginBatchAsync(CancellationToken.None);
        await fixture.Sink.WriteBatchItemAsync(new JsonRpcSuccessResponse { Id = 1, Result = "0x1" }, new RpcReport("eth_blockNumber", 1, true), CancellationToken.None);

        Assert.That(fixture.Sink.StopRequested, Is.EqualTo(expectedStopRequested));

        await fixture.Sink.EndBatchAsync(CancellationToken.None);
    }

    private static SocketSinkFixture CreateSocketSink(RpcEndpoint endpoint = RpcEndpoint.Ws, long maxBatchResponseBodySize = 10_000)
    {
        MemoryMessageStream stream = new();
        SemaphoreSlim sendSemaphore = new(1, 1);
        SocketJsonRpcResponseSink<MemoryMessageStream> sink = new(stream, new NullJsonRpcLocalStats(), maxBatchResponseBodySize, sendSemaphore, new JsonRpcContext(endpoint));
        return new SocketSinkFixture(stream, sendSemaphore, sink);
    }

    private readonly record struct SocketSinkFixture(MemoryMessageStream Stream, SemaphoreSlim SendSemaphore, SocketJsonRpcResponseSink<MemoryMessageStream> Sink) : IDisposable
    {
        public void Dispose()
        {
            Sink.Dispose();
            SendSemaphore.Dispose();
            Stream.Dispose();
        }
    }

    public class UsingIpc
    {
        [Test]
        [Explicit("Takes too long to run")]
        public async Task Can_handle_very_large_objects()
        {
            IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

            Task<int> receiveBytes = OneShotServer(ipEndPoint, CountNumberOfBytes);

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

            Task<int> receiveMessages = OneShotServer(ipEndPoint, CountNumberOfMessages);

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

                Assert.That(disposeCount, Is.EqualTo(messageCount));

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
            TaskCompletionSource completeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            async ValueTask ResponseFunc(CallInfo c)
            {
                Interlocked.Increment(ref concurrentCall);
                await completeSource.Task;
                IJsonRpcResponseSink sink = c.Arg<IJsonRpcResponseSink>();
                await sink.WriteSingleAsync(new JsonRpcSuccessResponse(null), new RpcReport(), c.Arg<CancellationToken>());
            }

            jsonRpcProcessor
                .ProcessAsync(
                    Arg.Any<PipeReader>(),
                    Arg.Any<JsonRpcContext>(),
                    Arg.Any<IJsonRpcResponseSink>(),
                    Arg.Any<JsonRpcProcessingOptions>(),
                    Arg.Any<CancellationToken>())
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

            Assert.That(processedRequests, Is.EqualTo(0));

            await sendStream.WriteAsync(requestBytes.AsMemory(1), pair.Cts.Token);
            await sendStream.WriteEndOfMessageAsync();

            Assert.That(() => processedRequests, Is.EqualTo(1).After(5000, 10));
            Assert.That(processedRequestSize, Is.EqualTo(requestBytes.Length));

            await ShutdownAndWait(pair.SendSocket, receiver);
        }

        private static IEnumerable<TestCaseData> CompleteJsonMessageCases()
        {
            string blockNumber = CreateJsonRequest(1, "eth_blockNumber");
            string chainId = CreateJsonRequest(2, "eth_chainId");
            yield return CompleteJsonMessageCase([blockNumber], "Single_json_without_delimiter");
            yield return CompleteJsonMessageCase([blockNumber, chainId], "Two_json_documents_without_delimiter");

            // Both messages >4KB to span multiple SocketClient buffer reads, triggering incremental JSON state
            string request1 = CreateJsonRequest(1, "eth_call", $"[\"{new string('x', 5000)}\"]");
            string request2 = CreateJsonRequest(2, "eth_call", $"[\"{new string('y', 5000)}\"]");
            yield return CompleteJsonMessageCase([request1, request2], "Json_parse_state_resets_between_consecutive_messages");

            yield return LargeChunkedJsonMessageCase(5);
            yield return LargeChunkedJsonMessageCase(10);
        }

        [TestCaseSource(nameof(CompleteJsonMessageCases))]
        public async Task Can_process_complete_json_messages_without_delimiter(string wireData, string[] expectedPayloads, int timeout, int chunkSize) =>
            await SendAndAssertPayloads(wireData, expectedPayloads, timeout, chunkSize);

        private static TestCaseData LargeChunkedJsonMessageCase(int messageCount)
        {
            string[] expectedPayloads = new string[messageCount];
            for (int i = 0; i < messageCount; i++)
            {
                string payload = new((char)('a' + i % 26), 10_000);
                expectedPayloads[i] = CreateJsonRequest(i, "eth_call", $"[\"{payload}\"]");
            }

            return CompleteJsonMessageCase(expectedPayloads, $"Large_chunked_json_without_delimiter_{messageCount}", timeout: 10000, chunkSize: 4096);
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
            string msg2 = CreateJsonRequestWithoutVersion(2, "eth_call", $"[\"{large2}\"]");
            string msg3 = CreateJsonRequestWithoutVersion(3, "eth_call");

            await SendAndAssertPayloads(msg1 + "\n" + msg2 + msg3, [msg1, msg2, msg3], timeout: 10000);
        }

        private static IEnumerable<TestCaseData> JsonBoundaryDetectionCases()
        {
            string json1 = CreateJsonRequest(1, "eth_blockNumber");
            string json2 = CreateJsonRequest(2, "eth_chainId");

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

        private static TestCaseData CompleteJsonMessageCase(string[] expectedPayloads, string name, int timeout = 5000, int chunkSize = 0) =>
            new TestCaseData(string.Concat(expectedPayloads), expectedPayloads, timeout, chunkSize).SetName(name);

        private static string CreateJsonRequest(int id, string method, string paramsJson = "[]") =>
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

        private static string CreateJsonRequestWithoutVersion(int id, string method, string paramsJson = "[]") =>
            $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

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

            Task<int> receiveMessages = OneShotServer(ipEndPoint, socket => ReadMessages(socket, receivedMessages));

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

        private static async Task SendAndAssertPayloads(string wireData, IReadOnlyList<string> expectedPayloads, int timeout = 5000, int chunkSize = 0)
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
            byte[] wireBytes = Encoding.UTF8.GetBytes(wireData);
            if (chunkSize <= 0)
            {
                await sendStream.WriteAsync(wireBytes, pair.Cts.Token);
            }
            else
            {
                for (int offset = 0; offset < wireBytes.Length; offset += chunkSize)
                {
                    int length = Math.Min(chunkSize, wireBytes.Length - offset);
                    await sendStream.WriteAsync(wireBytes.AsMemory(offset, length), pair.Cts.Token);
                    await Task.Delay(1, pair.Cts.Token);
                }
            }

            Assert.That(() => processedRequests, Is.EqualTo(expectedPayloads.Count).After(timeout, 10));
            Assert.That(processedPayloads, Is.EqualTo(expectedPayloads));

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
            async ValueTask ResponseFunc(CallInfo callInfo)
            {
                PipeReader reader = callInfo.ArgAt<PipeReader>(0);
                ReadResult readResult = await reader.ReadToEndAsync();
                ReadOnlySequence<byte> buffer = readResult.Buffer;
                onRequest(buffer);
                reader.AdvanceTo(buffer.End);
                IJsonRpcResponseSink sink = callInfo.Arg<IJsonRpcResponseSink>();
                await sink.WriteSingleAsync(new JsonRpcSuccessResponse(null), new RpcReport(), callInfo.Arg<CancellationToken>());
            }
            processor
                .ProcessAsync(
                    Arg.Any<PipeReader>(),
                    Arg.Any<JsonRpcContext>(),
                    Arg.Any<IJsonRpcResponseSink>(),
                    Arg.Any<JsonRpcProcessingOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(ResponseFunc);
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
            using CancellationTokenSource cts = new();

            Task<int> receiveMessages = OneShotServer("http://localhost:1337/", webSocket => CountMessages(webSocket, cts.Token));

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
            using CancellationTokenSource cts = new();

            Task<int> server = OneShotServer("http://localhost:1337/", webSocket => CountMessages(webSocket, cts.Token));

            Task sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync();
                await SendRandomBatchAsync(ws.Stream, RpcEndpoint.Ws, maxBatchResponseBodySize: null, elements, 100);
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
            using CancellationTokenSource cts = new();

            Task<long> receiveBytes = OneShotServer("http://localhost:1337/", webSocket => CountBytes(webSocket, cts.Token));

            Task<int> sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync(maxBatchResponseBodySize: maxByteCount);
                int sent = (int)await SendRandomBatchAsync(ws.Stream, RpcEndpoint.Ws, maxByteCount, 10, 100);
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
            await SendRandomBatchAsync(tc.Stream, RpcEndpoint.Ws, maxBatchResponseBodySize: 10_000, 10, 100);
            stream.Seek(0, SeekOrigin.Begin);
            JsonRpcSuccessResponse[]? response = new EthereumJsonSerializer().Deserialize<JsonRpcSuccessResponse[]>(stream);
            Assert.That(response, Has.None.Null);
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

    private static async ValueTask<long> SendRandomBatchAsync<TStream>(
        TStream stream,
        RpcEndpoint endpoint,
        long? maxBatchResponseBodySize,
        int items,
        int size,
        CancellationToken cancellationToken = default)
        where TStream : Stream, IMessageBorderPreservingStream
    {
        using SemaphoreSlim sendSemaphore = new(1, 1);
        using SocketJsonRpcResponseSink<TStream> sink = new(
            stream,
            new NullJsonRpcLocalStats(),
            maxBatchResponseBodySize,
            sendSemaphore,
            new JsonRpcContext(endpoint));

        await sink.BeginBatchAsync(cancellationToken);
        for (int index = 0; index < items; index++)
        {
            await sink.WriteBatchItemAsync(RandomSuccessResponse(size), default, cancellationToken);
        }
        await sink.EndBatchAsync(cancellationToken);

        return sink.BytesWritten;
    }

    private static JsonRpcSuccessResponse RandomSuccessResponse(int size, Action? disposeAction = null) =>
        new(disposeAction)
        {
            Id = "42",
            Result = RandomObject(size)
        };

    private static object RandomObject(int size)
    {
        byte[] rawBytes = RandomRawBytes(size / 2 * 32);
        return new GethLikeTxTrace
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = (ReadOnlyMemory<byte>?)rawBytes, Memory = (ReadOnlyMemory<byte>?)rawBytes,
                }
            }
        };
    }

    private static byte[] RandomRawBytes(int byteLength, bool runGc = true)
    {
        Random random = new();
        byte[] bytes = new byte[byteLength];
        random.NextBytes(bytes);
        if (runGc) GC.Collect();
        return bytes;
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
        public TStream Stream { get; } = stream;

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
