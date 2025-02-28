// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class WebsocketHandler: ISocketHandler
{
    private readonly WebSocket _webSocket;
    public PipeReader PipeReader { get; }
    public PipeWriter PipeWriter { get; }
    private Pipe _writerPipe { get; }
    private Pipe _readerPipe { get; }

    private Lock _messageBoundaryLock = new();
    private long _messageSize = 0;
    private long _writtenBytes = 0;
    private TaskCompletionSource _writtenToBoundary = new(); // Probably fine to leave sync continuation as if here

    private bool _isClosed = false;

    public WebsocketHandler(WebSocket webSocket)
    {
        _webSocket = webSocket;

        _writerPipe = new Pipe();
        PipeWriter = _writerPipe.Writer;

        _readerPipe = new Pipe();
        PipeReader = _readerPipe.Reader;
    }

    public Task Start(CancellationToken cancellationToken)
    {
        Task readFromWebsocket = CloseOnError(ReadFromWebsocket(cancellationToken));
        // On finish need to closea websocket
        // await _writerPipe.Writer.CompleteAsync();

        Task writeWebsocketTask = CloseOnError(WriteToWebsocketTask(cancellationToken));
        // Graceful shutdown
        // await _writerPipe.Writer.CompleteAsync();
        // await writeWebsocketTask;

        return Task.WhenAll(readFromWebsocket, writeWebsocketTask);
    }

    /// <summary>
    /// Mainly to unblock other code so that the exception can bubble up in `Start`.
    /// </summary>
    /// <param name="task"></param>
    private async Task CloseOnError(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            _isClosed = true;
            _writtenToBoundary.TrySetResult();
            throw;
        }
    }

    private async Task ReadFromWebsocket(CancellationToken cancellationToken)
    {
        PipeWriter readerWriter = _readerPipe.Writer;

        try
        {
            while (!_isClosed)
            {
                Memory<byte> memory = readerWriter.GetMemory(1024);
                ValueWebSocketReceiveResult result = await _webSocket.ReceiveAsync(memory, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                readerWriter.Advance(result.Count);
            }
        }
        finally
        {
            await readerWriter.CompleteAsync();
        }
    }

    private async Task WriteToWebsocketTask(CancellationToken cancellationToken)
    {
        PipeReader writerPipeReader = _writerPipe.Reader;

        while (!_isClosed)
        {
            ReadResult result;
            result = await writerPipeReader.ReadAsync(cancellationToken);
            if (result.IsCanceled) break;
            if (result.IsCompleted)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Remote close", cancellationToken);
                break;
            }

            if (result.Buffer.Length == 0) continue;

            foreach (var readOnlyMemory in result.Buffer)
            {
                await _webSocket.SendAsync(readOnlyMemory, WebSocketMessageType.Binary, endOfMessage: false,
                    cancellationToken);
            }
            writerPipeReader.AdvanceTo(result.Buffer.End);

            bool shouldSendEom = false;
            using (_messageBoundaryLock.EnterScope())
            {
                _writtenBytes += result.Buffer.Length;
                // Written enough to reach full message
                if (_writtenBytes == _messageSize)
                {
                    shouldSendEom = true;
                    _messageSize = 0;
                    _writtenBytes = 0;
                }
            }

            if (shouldSendEom)
            {
                await _webSocket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true,
                    CancellationToken.None);
                _writtenToBoundary.SetResult();
            }
        }
    }

    public async Task WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        await pipeWriter.FlushAsync(cancellationToken);
        if (pipeWriter.WrittenCount == 0) return;
        if (_isClosed) return;

        bool shouldSendEom = false;
        using (_messageBoundaryLock.EnterScope())
        {
            _messageSize = pipeWriter.WrittenCount;
            // Written enough to reach full message
            if (_writtenBytes == _messageSize)
            {
                shouldSendEom = true;
                _messageSize = 0;
                _writtenBytes = 0;
            }
        }

        if (shouldSendEom)
        {
            await _webSocket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true,
                CancellationToken.None);
            _writtenToBoundary = new TaskCompletionSource();
        }
        else
        {
            await using (cancellationToken.Register(() => _writtenToBoundary.TrySetCanceled())) {
                await _writtenToBoundary.Task;
            }
            _writtenToBoundary = new TaskCompletionSource();
        }
    }

    public ValueTask DisposeAsync()
    {
        _webSocket.Dispose();
        return ValueTask.CompletedTask;
    }
}
