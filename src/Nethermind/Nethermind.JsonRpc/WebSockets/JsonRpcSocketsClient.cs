// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class JsonRpcSocketsClient<TStream> : SocketClient<TStream>, IJsonRpcDuplexClient where TStream : Stream, IMessageBorderPreservingStream
{
    public event EventHandler? Closed;

    private readonly IJsonRpcProcessor _jsonRpcProcessor;
    private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
    private readonly long? _maxBatchResponseBodySize;
    private readonly JsonRpcContext _jsonRpcContext;

    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly Channel<ProcessRequest> _processChannel;

    private sealed record ProcessRequest(Memory<byte> Buffer, IMemoryOwner<byte> BufferOwner) : IAsyncDisposable
    {
        private bool _disposed;

        public ValueTask DisposeAsync()
        {
            if (!Interlocked.CompareExchange(ref _disposed, true, false)) BufferOwner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly int _workerTaskCount = 1;

    public JsonRpcSocketsClient(
        string clientName,
        TStream stream,
        RpcEndpoint endpointType,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        JsonRpcUrl? url = null,
        long? maxBatchResponseBodySize = null,
        int concurrency = 1)
        : base(clientName, stream, jsonSerializer)
    {
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _maxBatchResponseBodySize = maxBatchResponseBodySize;
        _jsonRpcContext = new JsonRpcContext(endpointType, this, url);
        _processChannel = Channel.CreateBounded<ProcessRequest>(new BoundedChannelOptions(concurrency)
        {
            SingleWriter = true
        });
        _workerTaskCount = concurrency;
    }

    public override void Dispose()
    {
        base.Dispose();
        _sendSemaphore.Dispose();
        _jsonRpcContext.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public override async Task ProcessAsync(ArraySegment<byte> data, CancellationToken cancellationToken)
    {
        IncrementBytesReceivedMetric(data.Count);

        IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(data.Count);
        data.AsSpan().CopyTo(memoryOwner.Memory.Span);
        Memory<byte> memory = memoryOwner.Memory[..data.Count];

        await _processChannel.Writer.WriteAsync(new ProcessRequest(memory, memoryOwner), cancellationToken);
    }

    public override async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        using ArrayPoolList<Task> allTasks = new(_workerTaskCount + 1);
        allTasks.Add(Task.Run(async () =>
        {
            try
            {
                await base.ReceiveLoopAsync(cts.Token);
            }
            finally
            {
                _processChannel.Writer.Complete();
            }
        }));

        for (int i = 0; i < _workerTaskCount; i++)
        {
            allTasks.Add(Task.Run(() => WorkerLoop(cts.Token)));
        }

        await cts.WhenAllSucceed(allTasks);
    }

    private async Task WorkerLoop(CancellationToken cancellationToken)
    {
        await foreach (ProcessRequest request in _processChannel.Reader.ReadAllAsync(cancellationToken))
        {
            await using ProcessRequest _ = request;
            await HandleRequest(request.Buffer, cancellationToken);
        }
    }

    private async Task HandleRequest(Memory<byte> data, CancellationToken cancellationToken = default)
    {
        PipeReader request = PipeReader.Create(new ReadOnlySequence<byte>(data));
        using SocketJsonRpcResponseSink<TStream> sink = new(
            _stream,
            _jsonRpcLocalStats,
            _maxBatchResponseBodySize,
            _sendSemaphore,
            _jsonRpcContext);

        await _jsonRpcProcessor.ProcessAsync(
            request,
            _jsonRpcContext,
            sink,
            new JsonRpcProcessingOptions(JsonRpcInputMode.MultipleDocuments),
            cancellationToken);

        IncrementBytesSentMetric(sink.BytesWritten);
    }

    private void IncrementBytesReceivedMetric(long size) =>
        IncrementBytesMetric(size, ref Metrics.JsonRpcBytesReceivedWebSockets, ref Metrics.JsonRpcBytesReceivedIpc);

    private void IncrementBytesSentMetric(long size) =>
        IncrementBytesMetric(size, ref Metrics.JsonRpcBytesSentWebSockets, ref Metrics.JsonRpcBytesSentIpc);

    private void IncrementBytesMetric(long size, ref long webSocketsMetric, ref long ipcMetric)
    {
        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.Ws) Interlocked.Add(ref webSocketsMetric, size);
        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.IPC) Interlocked.Add(ref ipcMetric, size);
    }

    public virtual async Task<int> SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            JsonRpcResponse response = result.Response ?? throw new InvalidOperationException("JSON-RPC result does not contain a response.");
            long responseSize = await SocketJsonRpcResponseWriter.WriteMessageAsync(_stream, response, cancellationToken);
            return (int)responseSize;
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }
}
