// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
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

    private record ProcessRequest(Memory<byte> Buffer, IMemoryOwner<byte> BufferOwner) : IAsyncDisposable
    {
        private bool _disposed = false;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, true, false) == true) return ValueTask.CompletedTask;
            BufferOwner.Dispose();
            return ValueTask.CompletedTask;
        }
    };

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

    private static readonly byte[] _jsonOpeningBracket = [Convert.ToByte('[')];
    private static readonly byte[] _jsonComma = [Convert.ToByte(',')];
    private static readonly byte[] _jsonClosingBracket = [Convert.ToByte(']')];

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
            allTasks.Add(Task.Run(async () => await WorkerLoop(cts.Token)));
        }

        await cts.WhenAllSucceed(allTasks);
    }

    private async Task WorkerLoop(CancellationToken cancellationToken)
    {
        await foreach (ProcessRequest request in _processChannel.Reader.ReadAllAsync(cancellationToken))
        {
            await using var _ = request;
            await HandleRequest(request.Buffer, cancellationToken);
        }
    }

    private async Task HandleRequest(Memory<byte> data, CancellationToken cancellationToken = default)
    {
        PipeReader request = PipeReader.Create(new ReadOnlySequence<byte>(data));
        int allResponsesSize = 0;

        await foreach (JsonRpcResult result in _jsonRpcProcessor.ProcessAsync(request, _jsonRpcContext).WithCancellation(cancellationToken))
        {
            using (result)
            {
                int singleResponseSize = await SendJsonRpcResult(result, cancellationToken);
                allResponsesSize += singleResponseSize;

                long startTime = Stopwatch.GetTimestamp();

                if (result.IsCollection)
                {
                    long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                    _ = _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, singleResponseSize);
                }
                else
                {
                    long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                    _ = _jsonRpcLocalStats.ReportCall(result.Report!.Value, handlingTimeMicroseconds, singleResponseSize);
                }
            }
        }

        IncrementBytesSentMetric(allResponsesSize);
    }

    private void IncrementBytesReceivedMetric(int size)
    {
        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.Ws)
        {
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedWebSockets, size);
        }

        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.IPC)
        {
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedIpc, size);
        }
    }

    private void IncrementBytesSentMetric(int size)
    {
        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.Ws)
        {
            Interlocked.Add(ref Metrics.JsonRpcBytesSentWebSockets, size);
        }

        if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.IPC)
        {
            Interlocked.Add(ref Metrics.JsonRpcBytesSentIpc, size);
        }
    }

    public virtual async Task<int> SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (result.IsCollection)
            {
                int responseSize = 1;
                bool isFirst = true;
                await _stream.WriteAsync(_jsonOpeningBracket, cancellationToken);
                await using JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses!.GetAsyncEnumerator(cancellationToken);
                while (await enumerator.MoveNextAsync())
                {
                    JsonRpcResult.Entry entry = enumerator.Current;
                    using (entry)
                    {
                        if (!isFirst)
                        {
                            await _stream.WriteAsync(_jsonComma, cancellationToken);
                            responseSize += 1;
                        }
                        isFirst = false;
                        responseSize += (int)await _jsonSerializer.SerializeAsync(_stream, entry.Response, cancellationToken, indented: false);
                        _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                        // We reached the limit and don't want to responded to more request in the batch
                        if (!_jsonRpcContext.IsAuthenticated && responseSize > _maxBatchResponseBodySize)
                        {
                            enumerator.IsStopped = true;
                        }
                    }
                }

                await _stream.WriteAsync(_jsonClosingBracket);
                responseSize++;

                responseSize += await _stream.WriteEndOfMessageAsync();

                return responseSize;
            }
            else
            {
                int responseSize = (int)await _jsonSerializer.SerializeAsync(_stream, result.Response, cancellationToken, indented: false);
                responseSize += await _stream.WriteEndOfMessageAsync();
                return responseSize;
            }
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }
}
