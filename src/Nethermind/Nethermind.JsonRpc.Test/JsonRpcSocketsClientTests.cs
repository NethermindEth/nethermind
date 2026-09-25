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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.IO;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Core.Memory;
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
    private readonly GCKeeper _keeper = new(NoGCStrategy.Instance, NullLogManager.Instance);

    [OneTimeTearDown]
    public void DisposeKeeper() => _keeper.Dispose();

    private JsonRpcService.StreamingContext CreateStreamingContext(int id = 42) => new(
        new JsonRpcService(NullModuleProvider.Instance, NullLogManager.Instance, new JsonRpcConfig(), _keeper),
        new JsonRpcRequest { Id = new JsonRpcId((long)id), Method = "trace_call" }, "trace_call");

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
        SocketSendLock sendSemaphore = new();
        SocketJsonRpcResponseSink<MemoryMessageStream> sink = new(stream, new NullJsonRpcLocalStats(), maxBatchResponseBodySize, sendSemaphore, new JsonRpcContext(endpoint));
        return new SocketSinkFixture(stream, sendSemaphore, sink);
    }

    private readonly record struct SocketSinkFixture(MemoryMessageStream Stream, SocketSendLock SendSemaphore, SocketJsonRpcResponseSink<MemoryMessageStream> Sink) : IDisposable
    {
        public void Dispose()
        {
            Sink.Dispose();
            SendSemaphore.Dispose();
            Stream.Dispose();
        }
    }

    [Test]
    public async Task WebSocket_timeout_replaces_uncommitted_result_or_fails_receive_loop([Values] bool committed, [Values] bool batch)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(listener.LocalEndPoint!, deadline.Token);
        using Socket serverSocket = await listener.AcceptAsync(deadline.Token);
        using WebSocket clientWebSocket = WebSocket.CreateFromStream(new NetworkStream(clientSocket, ownsSocket: true), isServer: false, subProtocol: null, TimeSpan.Zero);
        using WebSocket serverWebSocket = WebSocket.CreateFromStream(new NetworkStream(serverSocket, ownsSocket: true), isServer: true, subProtocol: null, TimeSpan.Zero);
        IJsonRpcProcessor processor = Substitute.For<IJsonRpcProcessor>();
        int requests = 0;
        TaskCompletionSource prefixWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failureObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowTeardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.ProcessAsync(Arg.Any<PipeReader>(), Arg.Any<JsonRpcContext>(), Arg.Any<IJsonRpcResponseSink>(),
            Arg.Any<JsonRpcProcessingOptions>(), Arg.Any<CancellationToken>()).Returns(Respond);
        async ValueTask Respond(CallInfo call)
        {
            requests++;
            using JsonRpcSuccessResponse response = new()
            {
                Id = requests,
                Result = requests == 1 ? new TimedOutStreamable(committed, committed ? async () =>
                {
                    prefixWritten.SetResult();
                    await failWrite.Task.WaitAsync(deadline.Token);
                }
                : null) : "ok",
                Streaming = CreateStreamingContext(requests)
            };
            IJsonRpcResponseSink sink = call.Arg<IJsonRpcResponseSink>();
            CancellationToken token = call.Arg<CancellationToken>();
            try
            {
                if (batch)
                {
                    await sink.BeginBatchAsync(token);
                    await sink.WriteBatchItemAsync(response, new RpcReport("trace_call", 0, true), token);
                    await sink.EndBatchAsync(token);
                }
                else await sink.WriteSingleAsync(response, new RpcReport("trace_call", 0, true), token);
            }
            catch (OperationCanceledException) when (committed)
            {
                // Hold worker teardown back after releasing the send lock to expose queued senders.
                ((IDisposable)sink).Dispose();
                failureObserved.SetResult();
                await allowTeardown.Task.WaitAsync(deadline.Token);
                throw;
            }
        }
        using TestClient<WebSocketMessageStream> server = new(new WebSocketMessageStream(serverWebSocket, LimboLogs.Instance), RpcEndpoint.Ws, processor);
        Task receiveLoop = server.Client.ReceiveLoopAsync(deadline.Token);
        try
        {
            await clientWebSocket.SendAsync("{}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            if (committed)
            {
                await prefixWritten.Task.WaitAsync(deadline.Token);
                using JsonRpcResult notification = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = "notification" }, default);
                Task<int> queuedSend = server.Client.SendJsonRpcResult(notification, deadline.Token);
                Assert.That(queuedSend.IsCompleted, Is.False);
                failWrite.SetResult();
                await failureObserved.Task.WaitAsync(deadline.Token);
                Assert.ThrowsAsync<IOException>(async () => await queuedSend.WaitAsync(deadline.Token));
                Assert.ThrowsAsync<IOException>(async () => await server.Client.SendJsonRpcResult(notification, deadline.Token));
                allowTeardown.SetResult();
                Assert.CatchAsync<OperationCanceledException>(async () => await receiveLoop.WaitAsync(deadline.Token));
                Assert.That(deadline.IsCancellationRequested, Is.False, "the execution timeout must fail the worker, not the test deadline");
                serverWebSocket.Abort();
                Assert.CatchAsync<WebSocketException>(async () => await ReadMessage());
            }
            else
            {
                using JsonDocument error = JsonDocument.Parse(await ReadMessage());
                Assert.That((batch ? error.RootElement[0] : error.RootElement).GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.Timeout));
                await clientWebSocket.SendAsync("{}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
                using JsonDocument success = JsonDocument.Parse(await ReadMessage());
                Assert.That((batch ? success.RootElement[0] : success.RootElement).GetProperty("result").GetString(), Is.EqualTo("ok"));
            }
        }
        finally
        {
            await deadline.CancelAsync();
            serverWebSocket.Abort();
            await receiveLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.That(receiveLoop.Exception?.InnerException, Is.Null.Or.InstanceOf<OperationCanceledException>());
        }

        async Task<string> ReadMessage()
        {
            using MemoryStream body = new();
            byte[] buffer = new byte[4096];
            ValueWebSocketReceiveResult read;
            do
            {
                read = await clientWebSocket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                body.Write(buffer, 0, read.Count);
            } while (!read.EndOfMessage);
            return Encoding.UTF8.GetString(body.ToArray());
        }
    }

    [Test]
    public async Task Socket_sink_reports_deferred_failure([Values] bool committed, [Values] bool batch)
    {
        using MemoryMessageStream stream = new();
        using SocketSendLock semaphore = new();
        IJsonRpcLocalStats stats = Substitute.For<IJsonRpcLocalStats>();
        stats.IsEnabled.Returns(true);
        using SocketJsonRpcResponseSink<MemoryMessageStream> sink = new(stream, stats, null, semaphore, new JsonRpcContext(RpcEndpoint.Ws));
        using JsonRpcSuccessResponse response = new()
        {
            Result = new TimedOutStreamable(committed),
            Streaming = CreateStreamingContext()
        };
        if (batch) await sink.BeginBatchAsync(CancellationToken.None);
        async Task Write() => await (batch
            ? sink.WriteBatchItemAsync(response, new RpcReport("trace_call", 0, true), CancellationToken.None)
            : sink.WriteSingleAsync(response, new RpcReport("trace_call", 0, true), CancellationToken.None));
        if (committed) Assert.CatchAsync<OperationCanceledException>(Write);
        else
        {
            await Write();
            if (batch) await sink.EndBatchAsync(CancellationToken.None);
        }
        stats.Received(1).ReportCall(Arg.Is<RpcReport>(report => report.Method == "trace_call" && !report.Success), Arg.Any<long>(), Arg.Any<long?>());
        if (committed)
        {
            sink.Dispose();
            byte[] partial = stream.ToArray();
            using SocketJsonRpcResponseSink<MemoryMessageStream> next = new(stream, stats, null, semaphore, new JsonRpcContext(RpcEndpoint.Ws));
            using JsonRpcSuccessResponse nextResponse = new() { Result = "next" };
            Assert.ThrowsAsync<IOException>(async () => await next.WriteSingleAsync(nextResponse, default, CancellationToken.None));
            Assert.ThrowsAsync<IOException>(async () => await next.BeginBatchAsync(CancellationToken.None));
            AssertIncompleteMessageNotExtended(stream, partial);
        }
    }

    [Test]
    public void Failed_notification_prevents_further_sends()
    {
        using MemoryMessageStream stream = new();
        using TestClient<MemoryMessageStream> server = new(stream);
        using JsonRpcResult failed = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = new TimedOutStreamable(true) }, default);
        Exception original = Assert.CatchAsync<OperationCanceledException>(async () => await server.Client.SendJsonRpcResult(failed))!;
        byte[] partial = stream.ToArray();

        using JsonRpcResult next = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = "next" }, default);
        IOException later = Assert.ThrowsAsync<IOException>(async () => await server.Client.SendJsonRpcResult(next))!;
        Assert.That(later.InnerException, Is.SameAs(original));
        AssertIncompleteMessageNotExtended(stream, partial);
    }

    [Test]
    public async Task Concurrent_disposal_does_not_extend_an_incomplete_notification()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using MemoryMessageStream stream = new();
        using TestClient<MemoryMessageStream> server = new(stream);
        TaskCompletionSource prefixWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using JsonRpcResult failed = JsonRpcResult.Single(new JsonRpcSuccessResponse
        {
            Result = new TimedOutStreamable(true, async () =>
            {
                prefixWritten.SetResult();
                await failWrite.Task.WaitAsync(deadline.Token);
            })
        }, default);
        int closed = 0;
        server.Client.Closed += (_, _) => Interlocked.Increment(ref closed);

        Task<int> send = server.Client.SendJsonRpcResult(failed);
        await prefixWritten.Task.WaitAsync(deadline.Token);
        byte[] partial = stream.ToArray();

        await Task.WhenAll(Task.Run(server.Client.Dispose), Task.Run(server.Client.Dispose)).WaitAsync(deadline.Token);
        failWrite.SetResult();

        Assert.CatchAsync(async () => await send.WaitAsync(deadline.Token));
        using JsonRpcResult next = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = "next" }, default);
        Assert.CatchAsync(async () => await server.Client.SendJsonRpcResult(next).WaitAsync(deadline.Token));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deadline.IsCancellationRequested, Is.False);
            Assert.That(closed, Is.EqualTo(1));
            AssertIncompleteMessageNotExtended(stream, partial);
        }
    }

    [Test]
    public async Task Missing_notification_response_does_not_fault_the_connection()
    {
        using MemoryMessageStream stream = new();
        using TestClient<MemoryMessageStream> server = new(stream);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await server.Client.SendJsonRpcResult(default));
        using JsonRpcResult next = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = "next" }, default);

        await server.Client.SendJsonRpcResult(next);

        Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Does.Contain("next"));
    }

    [Test]
    public async Task Failed_notification_terminates_an_idle_receive_loop([Values] bool returnClosed)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        using PassiveMessageStream stream = new(returnClosed);
        using TestClient<PassiveMessageStream> server = new(stream);
        Task receiver = server.Client.ReceiveLoopAsync(deadline.Token);
        await stream.Receiving.Task.WaitAsync(deadline.Token);
        using JsonRpcResult failed = JsonRpcResult.Single(new JsonRpcSuccessResponse { Result = new TimedOutStreamable(true) }, default);

        OperationCanceledException? failure = Assert.CatchAsync<OperationCanceledException>(async () => await server.Client.SendJsonRpcResult(failed));
        IOException? stopped = Assert.ThrowsAsync<IOException>(async () => await receiver.WaitAsync(deadline.Token));
        Assert.That(stopped!.InnerException, Is.SameAs(failure));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deadline.IsCancellationRequested, Is.False, "the notification failure must stop the receiver");
            Assert.That(stream.CanWrite, Is.False, "the receive loop must dispose its transport");
        }
    }

    private sealed class PassiveMessageStream(bool returnClosed) : MemoryMessageStream, IMessageBorderPreservingStream
    {
        internal TaskCompletionSource Receiving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<ReceiveResult> IMessageBorderPreservingStream.ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Receiving.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (returnClosed)
            {
                return new ReceiveResult { Closed = true };
            }
            return new ReceiveResult { Closed = true };
        }
    }

    private static void AssertIncompleteMessageNotExtended(MemoryMessageStream stream, byte[] partial)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(partial.AsSpan().Count((byte)'\n'), Is.Zero, "the failed message must not be terminated");
            Assert.That(Encoding.UTF8.GetString(partial), Does.Not.Contain("unsent-tail"), "the failed tail must not be flushed");
            Assert.That(stream.ToArray(), Is.EqualTo(partial), "later sends must not extend the failed message");
        }
    }

    private sealed class TimedOutStreamable(bool committed, Func<Task>? beforeFailure = null) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write(Encoding.UTF8.GetBytes("[\"" + new string('x', committed ? 32 * 1024 : 0)));
            await writer.FlushAsync(cancellationToken);
            if (beforeFailure is not null) await beforeFailure();
            writer.Write("unsent-tail"u8);
            throw new OperationCanceledException();
        }
    }

    public class UsingIpc
    {
        [Test]
        [Explicit("Takes too long to run")]
        public async Task Can_handle_very_large_objects()
        {
            using Socket listener = ListenOnLoopback(out IPEndPoint ipEndPoint);

            Task<int> receiveBytes = OneShotServer(listener, CountNumberOfBytes);

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

        [Test]
        public async Task Can_send_multiple_messages([Values(1, 2, 10, 50)] int messageCount)
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

            using Socket listener = ListenOnLoopback(out IPEndPoint ipEndPoint);

            Task<int> receiveMessages = OneShotServer(listener, CountNumberOfMessages);

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

        [Test]
        public async Task Dispose_raises_Closed_once_when_called_twice()
        {
            using UnixSocketPair pair = await UnixSocketPair.CreateAsync();
            using TestClient<IpcSocketMessageStream> tc = new(new IpcSocketMessageStream(pair.SendSocket));
            int closed = 0;
            tc.Client.Closed += (_, _) => closed++;

            tc.Client.Dispose();
            tc.Client.Dispose();

            Assert.That(closed, Is.EqualTo(1));
        }

        [Test]
        public async Task CanHandleMessageConcurrently([Values(1, 5)] int concurrencyLevel)
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

        [Test]
        public async Task Fuzz_messages_integrity([Values(10, 63, 1024, 1024000)] int bufferSize)
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

            using Socket listener = ListenOnLoopback(out IPEndPoint ipEndPoint);

            List<byte[]> sentMessages = [];
            List<byte[]> receivedMessages = [];

            Task<int> receiveMessages = OneShotServer(listener, socket => ReadMessages(socket, receivedMessages));

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

        /// <summary>
        /// Binds a loopback listener on an OS-assigned port and reports the endpoint it actually got, so two
        /// concurrent runs of this assembly cannot collide on one port.
        /// </summary>
        /// <remarks>
        /// Binding happens here rather than inside <c>OneShotServer</c> so that the listener is already
        /// accepting before the test's client task starts connecting.
        /// </remarks>
        private static Socket ListenOnLoopback(out IPEndPoint boundEndPoint)
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen();
                boundEndPoint = (IPEndPoint)listener.LocalEndPoint!;
                return listener;
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        private static async Task<T> OneShotServer<T>(Socket listener, Func<Socket, Task<T>> func)
        {
            Socket handler = await listener.AcceptAsync();

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
        public async Task Can_send_multiple_messages([Values(2, 10, 50)] int messageCount)
        {
            using CancellationTokenSource cts = new();

            int port = FindFreeLoopbackPort();
            Task<int> receiveMessages = OneShotServer($"http://localhost:{port}/", webSocket => CountMessages(webSocket, cts.Token));

            Task<int> sendMessages = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync(port);
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

        [Test]
        public async Task Can_send_collections([Values(2, 10, 50)] int elements)
        {
            using CancellationTokenSource cts = new();

            int port = FindFreeLoopbackPort();
            Task<int> server = OneShotServer($"http://localhost:{port}/", webSocket => CountMessages(webSocket, cts.Token));

            Task sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync(port);
                await SendRandomBatchAsync(ws.Stream, RpcEndpoint.Ws, maxBatchResponseBodySize: null, elements, 100);
                await Task.Delay(100);
                await cts.CancelAsync();
            });

            await Task.WhenAll(sendCollection, server);
            Assert.That(server.Result, Is.EqualTo(1));
        }

        [Test]
        [Ignore("Feature does not work correctly")]
        public async Task Stops_on_limited_body_size([Values(1_000, 5_000, 10_000)] int maxByteCount)
        {
            using CancellationTokenSource cts = new();

            int port = FindFreeLoopbackPort();
            Task<long> receiveBytes = OneShotServer($"http://localhost:{port}/", webSocket => CountBytes(webSocket, cts.Token));

            Task<int> sendCollection = Task.Run(async () =>
            {
                using TestClient<WebSocketMessageStream> ws = await TestClient.ConnectWsAsync(port, maxBatchResponseBodySize: maxByteCount);
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

        /// <summary>
        /// Returns a loopback port that was free a moment ago.
        /// </summary>
        /// <remarks>
        /// Inherently racy: <see cref="HttpListener"/> prefixes cannot ask for port 0, so the port has to be named
        /// before the listener exists, and nothing holds it in between. It is better than a hard-coded port and no
        /// more than that. These cases are <c>[Explicit]</c> and do not run in CI, which is why this is tolerable
        /// here and not in <see cref="ListenOnLoopback"/>.
        /// </remarks>
        private static int FindFreeLoopbackPort()
        {
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint!).Port;
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
        using SocketSendLock sendSemaphore = new();
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

        public static async Task<TestClient<WebSocketMessageStream>> ConnectWsAsync(int port, long? maxBatchResponseBodySize = null)
        {
            ClientWebSocket socket = new();
            await socket.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
            WebSocketMessageStream stream = new(socket, NullLogManager.Instance);
            return new TestClient<WebSocketMessageStream>(stream, RpcEndpoint.Ws, maxBatchResponseBodySize: maxBatchResponseBodySize, owner: socket);
        }
    }
}
