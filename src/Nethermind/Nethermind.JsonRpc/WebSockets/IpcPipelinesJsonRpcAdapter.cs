// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.WebSockets;

public class IpcPipelinesJsonRpcAdapter : PipelinesJsonRpcAdapter
{
    private IpcPipelinesJsonRpcAdapter(
        RpcEndpoint endpointType,
        PipeReader pipeReader,
        PipeWriter pipeWriter,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        JsonRpcUrl? url = null
    ) : base(
        endpointType,
        pipeReader,
        pipeWriter,
        jsonRpcProcessor,
        jsonRpcLocalStats,
        jsonSerializer,
        options,
        url
    ) {
    }

    public static IpcPipelinesJsonRpcAdapter Create(
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        Socket socket)
    {
        var stream = new NetworkStream(socket);
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);

        return new IpcPipelinesJsonRpcAdapter(
            RpcEndpoint.IPC,
            reader,
            writer,
            jsonRpcProcessor,
            jsonRpcLocalStats,
            jsonSerializer,
            options);
    }

    public static async Task Start(
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        Socket socket,
        CancellationToken cancellationToken)
    {
        using PipelinesJsonRpcAdapter socketsClient = Create(jsonRpcProcessor, jsonRpcLocalStats, jsonSerializer, options, socket);
        await socketsClient.Start(cancellationToken);
    }

    private static readonly byte[] Delimiter = [Convert.ToByte('\n')];

    protected override Task<int> WriteEndOfMessage(PipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        pipeWriter.WriteAsync(Delimiter, cancellationToken);
        return Task.FromResult(0);
    }
}
