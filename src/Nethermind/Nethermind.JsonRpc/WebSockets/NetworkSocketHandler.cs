// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class NetworkSocketHandler : ISocketHandler
{
    private static readonly byte[] Delimiter = [Convert.ToByte('\n')];
    private readonly Socket _socket;
    private readonly NetworkStream _networkStream;

    public NetworkSocketHandler(Socket socket)
    {
        _socket = socket;
        _networkStream = new NetworkStream(socket);

        PipeReader = PipeReader.Create(_networkStream);
        PipeWriter = PipeWriter.Create(_networkStream);
    }

    public async ValueTask DisposeAsync()
    {
        await _networkStream.FlushAsync();
        await _networkStream.DisposeAsync();
        await _socket.DisconnectAsync(false);
        _socket.Dispose();
    }

    public PipeReader PipeReader { get; }
    public PipeWriter PipeWriter { get; }

    public Task Start(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        await pipeWriter.WriteAsync(Delimiter, cancellationToken);
        await pipeWriter.FlushAsync(cancellationToken);
    }
}
