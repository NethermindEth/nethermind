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
    protected override PipeReader PipeReader { get; }
    protected override PipeWriter PipeWriter { get; }

    private IpcPipelinesJsonRpcAdapter(
        RpcEndpoint endpointType,
        Socket socket,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        JsonRpcUrl? url = null
    ) : base(
        endpointType,
        jsonRpcProcessor,
        jsonRpcLocalStats,
        jsonSerializer,
        options,
        url
    ) {
        var networkStream = new NetworkStream(socket);

        PipeReader = PipeReader.Create(networkStream);
        PipeWriter = PipeWriter.Create(networkStream);
    }

    public static IpcPipelinesJsonRpcAdapter Create(
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        Socket socket)
    {
        return new IpcPipelinesJsonRpcAdapter(
            RpcEndpoint.IPC,
            socket,
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

    protected override Task<int> WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken)
    {
        pipeWriter.WriteAsync(Delimiter, cancellationToken);
        return Task.FromResult(0);
    }
}
