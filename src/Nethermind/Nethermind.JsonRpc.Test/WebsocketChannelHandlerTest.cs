// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Serialization.Json;
using NSubstitute;
using NSubstitute.Extensions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class WebsocketChannelHandlerTest
{
    [Test]
    public async Task Should_BlockEndOfMessage_UntilEnoughBytesWritten()
    {
        using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(10.Seconds());

        TaskCompletionSource tcs = new TaskCompletionSource();

        WebSocket webSocket = Substitute.For<WebSocket>();
        webSocket.Configure()
            .SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), WebSocketMessageType.Binary, false, Arg.Any<CancellationToken>())
            .Returns((_) => new ValueTask(tcs.Task));
        webSocket.Configure()
            .ReceiveAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>())
            .Returns((_) => new ValueTask<ValueWebSocketReceiveResult>(Task.Run(async () =>
            {
                await cts.Token.AsTask();
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            })));

        WebsocketHandler websocketHandler = new WebsocketHandler(webSocket);
        _ = websocketHandler.Start(cts.Token);

        CountingPipeWriter writer = new CountingPipeWriter(websocketHandler.PipeWriter);

        Memory<byte> memory = writer.GetMemory(1024);
        memory.Span[..1024].Fill(1);
        writer.Advance(1024);

        Task eomTask = websocketHandler.WriteEndOfMessage(writer, cts.Token);
        await Task.Delay(100);

        eomTask.IsCompleted.Should().BeFalse();

        tcs.TrySetResult();

        await eomTask;
    }

    [Test]
    public async Task Should_NotBlockEndOfMessage_IfEnoughBytesAlreadyWritten()
    {
        using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(10.Seconds());

        WebSocket webSocket = Substitute.For<WebSocket>();
        webSocket.Configure()
            .SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), WebSocketMessageType.Binary, false, Arg.Any<CancellationToken>())
            .Returns((_) => ValueTask.CompletedTask);
        webSocket.Configure()
            .ReceiveAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>())
            .Returns((_) => new ValueTask<ValueWebSocketReceiveResult>(Task.Run(async () =>
            {
                await cts.Token.AsTask();
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            })));

        WebsocketHandler websocketHandler = new WebsocketHandler(webSocket);
        _ = websocketHandler.Start(cts.Token);

        CountingPipeWriter writer = new CountingPipeWriter(websocketHandler.PipeWriter);

        Memory<byte> memory = writer.GetMemory(1024);
        memory.Span[..1024].Fill(1);
        writer.Advance(1024);
        await websocketHandler.WriteEndOfMessage(writer, cts.Token);
    }
}
