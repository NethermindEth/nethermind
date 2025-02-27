// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class WebSocketPipelinesJsonRpcAdapter: PipelinesJsonRpcAdapter, ISocketsClient
{
    protected override PipeReader PipeReader { get; }
    protected override PipeWriter PipeWriter { get; }

    private readonly WebSocket _webSocket;
    private Pipe _writerPipe { get; }
    private Pipe _readerPipe { get; }

    private TaskCompletionSource _writtenToBoundary = new(); // Probably fine to leave sync continuation as if here
    private long _messageSize = 0;

    public WebSocketPipelinesJsonRpcAdapter(
        string clientId,
        WebSocket webSocket,
        RpcEndpoint endpointType,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        JsonRpcUrl? url = null
    ) : base(endpointType, jsonRpcProcessor, jsonRpcLocalStats, jsonSerializer, options, url)
    {
        _webSocket = webSocket;
        ClientName = clientId;

        _writerPipe = new Pipe();
        PipeWriter = _writerPipe.Writer;

        _readerPipe = new Pipe();
        PipeReader = _readerPipe.Reader;
    }

    public string ClientName { get; }

    public async Task SendAsync(SocketsMessage message)
    {
        if (message is null)
        {
            return;
        }

        if (message.Client == ClientName || string.IsNullOrWhiteSpace(ClientName) ||
            string.IsNullOrWhiteSpace(message.Client))
        {
            await SendJob(new { type = message.Type, client = ClientName, data = message.Data }, default);
        }
    }

    protected override async Task ReadTask(CancellationToken cancellationToken)
    {
        Task readFromWebsocket = ReadFromWebsocket(cancellationToken);
        try
        {
            await base.ReadTask(cancellationToken);
        }
        finally
        {
            await _writerPipe.Writer.CompleteAsync();
            await readFromWebsocket;
        }
    }

    private async Task ReadFromWebsocket(CancellationToken cancellationToken)
    {
        PipeWriter readerWriter = _readerPipe.Writer;

        while (true)
        {
            Memory<byte> memory = readerWriter.GetMemory(1024);
            ValueWebSocketReceiveResult result = await _webSocket.ReceiveAsync(memory, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await readerWriter.CompleteAsync();
                break;
            }

            readerWriter.Advance(result.Count);
        }
    }

    protected override async Task WriterTask(CancellationToken cancellationToken)
    {
        Task writeWebsocketTask = WriteToWebsocketTask(cancellationToken);

        try
        {
            await base.WriterTask(cancellationToken);
        }
        finally
        {
            // Graceful shutdown
            await _writerPipe.Writer.CompleteAsync();
            await writeWebsocketTask;
        }
    }

    private async Task WriteToWebsocketTask(CancellationToken cancellationToken)
    {
        PipeReader writerPipeReader = _writerPipe.Reader;
        long writtenBytes = 0;

        while (true)
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
                await _webSocket.SendAsync(readOnlyMemory, WebSocketMessageType.Binary, endOfMessage: false, cancellationToken);
            }
            writtenBytes += result.Buffer.Length;

            // Written enough to reach full message
            if (writtenBytes == _messageSize)
            {
                await _webSocket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, CancellationToken.None);
                _writtenToBoundary.SetResult();
                _messageSize = 0;
                writtenBytes = 0;
            }
        }
    }

    protected override async Task<int> WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        _messageSize = pipeWriter.WrittenCount;
        await _writerPipe.Writer.FlushAsync(cancellationToken);

        // Must not continue until the whole message has been written
        await using (cancellationToken.Register(() => _writtenToBoundary.TrySetCanceled())) {
            await _writtenToBoundary.Task;
        }

        _writtenToBoundary = new TaskCompletionSource();
        return 0;
    }
}
