// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class StreamSocketHandler: ISocketHandler
{
    private readonly Stream _stream;

    public StreamSocketHandler(Stream stream)
    {
        _stream = stream;

        PipeReader = PipeReader.Create(stream);
        PipeWriter = PipeWriter.Create(stream);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.FlushAsync();
        await _stream.DisposeAsync();
    }

    public PipeReader PipeReader { get; }
    public PipeWriter PipeWriter { get; }

    public Task Start(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        await pipeWriter.FlushAsync(cancellationToken);
    }
}
