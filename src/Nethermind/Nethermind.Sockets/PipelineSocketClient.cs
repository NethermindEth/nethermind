// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class PipelineSocketClient : ISocketsClient
{
    private readonly ISocketHandler _socketHandler;
    private readonly IJsonSerializer _jsonSerializer;

    protected readonly Channel<object> _writerChannel = Channel.CreateBounded<object>(new BoundedChannelOptions(1)
    {
        SingleReader = true
    });

    public PipelineSocketClient(string clientName, ISocketHandler handler, IJsonSerializer jsonSerializer)
    {
        ClientName = clientName;
        _socketHandler = handler;
        _jsonSerializer = jsonSerializer;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string ClientName { get; }

    public virtual Task Loop(CancellationToken token)
    {
        // Task writerTask = cts.CancelOnException(WriterTask(cts.Token));
        // // Task socketTask = cts.CancelOnException(_socketHandler.Start(cts.Token));
        Task writerTask = WriterTask(token);
        Task socketTask = _socketHandler.Start(token);
        return Task.WhenAll(writerTask, socketTask);
    }

    public async Task SendAsync(SocketsMessage message)
    {
        if (message is null)
        {
            return;
        }

        if (message.Client == ClientName || string.IsNullOrWhiteSpace(ClientName) ||
            string.IsNullOrWhiteSpace(message.Client))
        {

            await _writerChannel.Writer.WriteAsync(new { type = message.Type, client = ClientName, data = message.Data }, default);
        }
    }

    private async Task WriterTask(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in _writerChannel.Reader.ReadAllAsync(cancellationToken))
            {
                var countingPipeWriter = new CountingPipeWriter(_socketHandler.PipeWriter);
                await WriteResult(countingPipeWriter, result, cancellationToken);
                await _socketHandler.WriteEndOfMessage(countingPipeWriter, cancellationToken);
            }
        }
        finally
        {
            await _socketHandler.PipeWriter.FlushAsync(cancellationToken);
            await _socketHandler.PipeWriter.CompleteAsync();
        }
    }

    protected virtual async Task WriteResult(CountingPipeWriter countingPipeWriter, object result, CancellationToken cancellationToken)
    {
        await _jsonSerializer.SerializeAsync(countingPipeWriter, result, indented: false);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _socketHandler.DisposeAsync();
    }
}
