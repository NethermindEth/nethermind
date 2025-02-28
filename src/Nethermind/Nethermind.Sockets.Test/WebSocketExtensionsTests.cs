// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Sockets.Test;

public class WebSocketExtensionsTests
{
    private class WebSocketMock : WebSocket
    {
        private readonly Queue<(WebSocketReceiveResult, byte[]?)> _receiveResults;

        public WebSocketMock(Queue<WebSocketReceiveResult> receiveResults)
        {
            _receiveResults = new Queue<(WebSocketReceiveResult, byte[]?)>();
            foreach (var webSocketReceiveResult in receiveResults)
            {
                _receiveResults.Enqueue((webSocketReceiveResult, null));
            }
        }

        public WebSocketMock(Queue<(WebSocketReceiveResult, byte[]?)> receiveResults)
        {
            _receiveResults = receiveResults;
        }

        public override void Abort()
        {
            throw new NotImplementedException();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_receiveResults.Count == 0 && ReturnTaskWithFaultOnEmptyQueue)
            {
                throw new Exception();
            }

            (WebSocketReceiveResult res, byte[]? byteBuff) = _receiveResults.Dequeue();

            if (byteBuff is null)
            {
                // Had to use Array.Fill as it is more performant
                int length = Math.Min(buffer.Length, res.Count);
                if (length != 0)
                {
                    var span = buffer.Span.Slice(0, length);
                    span.Fill((byte)'0');
                    // Need to be a valid json
                    span[0] = (byte)'"';
                    span[length-1] = (byte)'"';
                }
            }
            else
            {
                byteBuff.CopyTo(buffer.Span);
            }

            return ValueTask.FromResult(new ValueWebSocketReceiveResult(res.Count, res.MessageType, res.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus { get; }
        public override string CloseStatusDescription { get; } = null!;
        public override WebSocketState State { get; } = WebSocketState.Open;
        public override string SubProtocol { get; } = null!;
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

        await using WebsocketHandler websocketHandler = new(mock);
        using AutoCancelTokenSource cts = new AutoCancelTokenSource();
        _ = websocketHandler.Start(cts.Token);
        ReadResult result = await websocketHandler.PipeReader.ReadToEndAsync();

        Assert.That(result.Buffer.Length, Is.EqualTo(2 * 4096 + 1024));
    }

    class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public async Task Updates_Metrics_And_Stats_Successfully()
    {
        Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
        receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(1024, WebSocketMessageType.Text, true));
        receiveResult.Enqueue(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        WebSocketMock mock = new(receiveResult);

        int reqCount = 0;

        var responses = new List<JsonRpcResult>()
        {
            (JsonRpcResult.Single((new JsonRpcResponse()), new RpcReport())),
            (JsonRpcResult.Collection(new JsonRpcBatchResult(static (e, c) =>
                new List<JsonRpcResult.Entry>()
                {
                    new(new JsonRpcResponse(), new RpcReport()),
                    new(new JsonRpcResponse(), new RpcReport()),
                    new(new JsonRpcResponse(), new RpcReport()),
                }.ToAsyncEnumerable().GetAsyncEnumerator(c))))
        };
        var processor = Substitute.For<IJsonRpcProcessor>();
        processor.HandleJsonParseResult(default, default!, default).ReturnsForAnyArgs((x) =>
        {
            return responses[reqCount++];
        });

        var localStats = Substitute.For<IJsonRpcLocalStats>();
        Metrics.JsonRpcBytesReceivedWebSockets = 0;
        Metrics.JsonRpcBytesSentWebSockets = 0;

        await using var webSocketsClient = new PipelinesJsonRpcAdapter(
            "TestClient",
            new WebsocketHandler(mock),
            RpcEndpoint.Ws,
            processor,
            localStats,
            new EthereumJsonSerializer(),
            new PipelinesJsonRpcAdapter.Options()
            {
                MaxJsonPayloadSize = 30.MB()
            },
            LimboLogs.Instance);

        await webSocketsClient.Loop(default);

        Assert.That(Metrics.JsonRpcBytesReceivedWebSockets, Is.EqualTo(1024 * 2));
        Assert.That(Metrics.JsonRpcBytesSentWebSockets, Is.EqualTo(112));
        await localStats.Received(2).ReportCall(Arg.Any<RpcReport>(), Arg.Any<long>(), Arg.Any<long>());
        await localStats.Received(1).ReportCall(Arg.Any<RpcReport>(), Arg.Any<long>(), 27);
        await localStats.Received(1).ReportCall(Arg.Any<RpcReport>(), Arg.Any<long>(), 85);
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
        var processor = Substitute.For<IJsonRpcProcessor>();
        processor.HandleJsonParseResult(default, default!, default).ReturnsForAnyArgs((x) =>
        {
            return JsonRpcResult.Single(new JsonRpcResult.Entry(new JsonRpcSuccessResponse() { Result = "ok" }, new RpcReport()));
        });
        await using var webSocketsClient = new PipelinesJsonRpcAdapter(
            "TestClient",
            new WebsocketHandler(mock),
            RpcEndpoint.Ws,
            processor,
            Substitute.For<IJsonRpcLocalStats>(),
            new EthereumJsonSerializer(),
            new PipelinesJsonRpcAdapter.Options()
            {
                MaxJsonPayloadSize = 30.MB()
            },
            LimboLogs.Instance);

        await webSocketsClient.Loop(default);
        await processor.Received(1000).HandleJsonParseResult(Arg.Any<JsonParseResult>(), Arg.Any<JsonRpcContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Can_receive_whole_message_non_buffer_sizes()
    {
        Queue<(WebSocketReceiveResult, byte[]?)> receiveResult = new Queue<(WebSocketReceiveResult, byte[]?)>();
        receiveResult.Enqueue((new WebSocketReceiveResult(1, WebSocketMessageType.Text, false), "["u8.ToArray()));
        byte[] messagePayload = new byte[1024];
        messagePayload.Fill((byte)'0');
        for (int i = 0; i < 6; i++)
        {
            receiveResult.Enqueue((new WebSocketReceiveResult(2000, WebSocketMessageType.Text, false), messagePayload));
        }
        receiveResult.Enqueue((new WebSocketReceiveResult(1, WebSocketMessageType.Text, true), "]"u8.ToArray()));
        receiveResult.Enqueue((new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), null));
        WebSocketMock mock = new(receiveResult);
        var processor = Substitute.For<IJsonRpcProcessor>();
        processor.HandleJsonParseResult(default, default!, default).ReturnsForAnyArgs((x) =>
        {
            return JsonRpcResult.Single(new JsonRpcResult.Entry(new JsonRpcSuccessResponse() { Result = "ok" }, new RpcReport()));
        });
        await using var webSocketsClient = new PipelinesJsonRpcAdapter(
            "TestClient",
            new WebsocketHandler(mock),
            RpcEndpoint.Ws,
            processor,
            Substitute.For<IJsonRpcLocalStats>(),
            new EthereumJsonSerializer(),
            new PipelinesJsonRpcAdapter.Options()
            {
                MaxJsonPayloadSize = 30.MB()
            },
            LimboLogs.Instance);

        await webSocketsClient.Loop(default);
        await processor.Received(1).HandleJsonParseResult(Arg.Any<JsonParseResult>(), Arg.Any<JsonRpcContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Throws_on_too_long_message()
    {
        Queue<(WebSocketReceiveResult, byte[]?)> receiveResult = new Queue<(WebSocketReceiveResult, byte[]?)>();
        receiveResult.Enqueue((new WebSocketReceiveResult(1, WebSocketMessageType.Text, false), "["u8.ToArray()));
        byte[] messagePayload = new byte[1024];
        messagePayload.Fill((byte)'0');
        for (int i = 0; i < 2048; i++)
        {
            receiveResult.Enqueue((new WebSocketReceiveResult(2000, WebSocketMessageType.Text, false), messagePayload));
        }
        receiveResult.Enqueue((new WebSocketReceiveResult(1, WebSocketMessageType.Text, true), "]"u8.ToArray()));
        receiveResult.Enqueue((new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), null));

        WebSocketMock mock = new(receiveResult);
        var processor = Substitute.For<IJsonRpcProcessor>();
        processor.HandleJsonParseResult(default, default!, default).ReturnsForAnyArgs((x) =>
        {
            return JsonRpcResult.Single(new JsonRpcResult.Entry(new JsonRpcSuccessResponse() { Result = "ok" }, new RpcReport()));
        });
        await using var webSocketsClient = new PipelinesJsonRpcAdapter(
            "TestClient",
            new WebsocketHandler(mock),
            RpcEndpoint.Ws,
            processor,
            Substitute.For<IJsonRpcLocalStats>(),
            new EthereumJsonSerializer(),
            new PipelinesJsonRpcAdapter.Options()
            {
                MaxJsonPayloadSize = 1.MB()
            },
            LimboLogs.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await webSocketsClient.Loop(default));
    }

    [Test, MaxTime(5000)]
    public async Task Stops_on_dirty_disconnect()
    {
        Queue<WebSocketReceiveResult> receiveResult = new Queue<WebSocketReceiveResult>();
        receiveResult.Enqueue(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        WebSocketMock mock = new(receiveResult);
        mock.ReturnTaskWithFaultOnEmptyQueue = true;

        var processor = Substitute.For<IJsonRpcProcessor>();
        processor.HandleJsonParseResult(default, default!, default).ReturnsForAnyArgs((x) =>
        {
            return JsonRpcResult.Single(new JsonRpcResult.Entry(new JsonRpcSuccessResponse() { Result = "ok" }, new RpcReport()));
        });
        await using var webSocketsClient = new PipelinesJsonRpcAdapter(
            "TestClient",
            new WebsocketHandler(mock),
            RpcEndpoint.Ws,
            processor,
            Substitute.For<IJsonRpcLocalStats>(),
            new EthereumJsonSerializer(),
            new PipelinesJsonRpcAdapter.Options()
            {
                MaxJsonPayloadSize = 1.MB()
            },
            LimboLogs.Instance);

        Assert.ThrowsAsync<Exception>(async () => await webSocketsClient.Loop(default));
    }
}
