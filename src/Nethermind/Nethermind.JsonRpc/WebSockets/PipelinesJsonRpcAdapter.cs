// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Configuration;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.WebSockets;

public abstract class PipelinesJsonRpcAdapter : IJsonRpcDuplexClient, IAsyncDisposable
{
    protected abstract PipeReader PipeReader { get; }
    protected abstract PipeWriter PipeWriter { get; }
    private readonly IJsonRpcProcessor _jsonRpcProcessor;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
    private readonly JsonRpcContext _jsonRpcContext;
    private readonly long? _maxBatchResponseBodySize;

    private static readonly byte[] _jsonOpeningBracket = [Convert.ToByte('[')];
    private static readonly byte[] _jsonComma = [Convert.ToByte(',')];
    private static readonly byte[] _jsonClosingBracket = [Convert.ToByte(']')];
    private AutoCancelTokenSource? _readCanceller;
    private TaskCompletionSource _exitedCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly int _processConcurrency = 1;

    private readonly Channel<JsonParseResult> _readerChannel = Channel.CreateBounded<JsonParseResult>(new BoundedChannelOptions(1)
    {
        SingleWriter = true
    });
    private readonly Channel<JsonRpcResult> _writerChannel = Channel.CreateBounded<JsonRpcResult>(new BoundedChannelOptions(1)
    {
        SingleReader = true
    });

    public record Options(
        long? MaxBatchResponseBodySize = null,
        int ProcessConcurrency = 1
    );

    public PipelinesJsonRpcAdapter(
        RpcEndpoint endpointType,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        JsonRpcUrl? url = null)
    {
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _jsonSerializer = jsonSerializer;
        _jsonRpcContext = new JsonRpcContext(endpointType, this, url);
        _maxBatchResponseBodySize = options.MaxBatchResponseBodySize;
        _processConcurrency = options.ProcessConcurrency;
    }

    public async Task Start(CancellationToken cancellationToken)
    {
        _readCanceller = cancellationToken.CreateChildTokenSource();
        using AutoCancelTokenSource cts = _readCanceller.Value.Token.CreateChildTokenSource();

        // Cancellation token specifically for read so that we can cancel just the read tasks which finishes
        // the channels.
        Task readTask = ReadTask(_readCanceller.Value.Token);
        Task processTask = ProcessTask(cts.Token);
        Task writerTask = WriterTask(cts.Token);

        try
        {
            // Must await so that cts is not cancelled
            await Task.WhenAll(readTask, processTask, writerTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _exitedCompletionSource.TrySetResult();
        }
    }

    protected virtual async Task ReadTask(CancellationToken cancellationToken)
    {
        await foreach (var jsonParseResult in JsonRpcUtils.MultiParseJsonDocument(PipeReader, cancellationToken))
        {
            IncrementBytesReceivedMetric((int)jsonParseResult.ReadSize);
            await _readerChannel.Writer.WriteAsync(jsonParseResult, cancellationToken);
        }
        _readerChannel.Writer.Complete();
    }

    private async Task ProcessTask(CancellationToken cancellationToken)
    {
        await Task.WhenAll(Enumerable.Range(0, _processConcurrency)
            .Select(async (_) =>
            {
                await ProcessTaskWorker(cancellationToken);
            }));

        _writerChannel.Writer.Complete();
    }

    private async Task ProcessTaskWorker(CancellationToken cancellationToken)
    {
        await foreach (var jsonParseResult in _readerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            JsonRpcResult? result = await _jsonRpcProcessor.HandleJsonParseResult(jsonParseResult, _jsonRpcContext, cancellationToken);
            if (result is not null)
            {
                await SendJsonRpcResult(result.Value, cancellationToken);
            }
        }
    }

    protected virtual async Task WriterTask(CancellationToken cancellationToken)
    {
        await foreach (var result in _writerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            var countingPipeWriter = new CountingPipeWriter(PipeWriter);
            if (result.IsCollection)
            {
                bool isFirst = true;
                await countingPipeWriter.WriteAsync(_jsonOpeningBracket, cancellationToken);
                await using JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses!.GetAsyncEnumerator(CancellationToken.None);
                while (await enumerator.MoveNextAsync())
                {
                    JsonRpcResult.Entry entry = enumerator.Current;
                    using (entry)
                    {
                        if (!isFirst)
                        {
                            await countingPipeWriter.WriteAsync(_jsonComma, cancellationToken);
                        }
                        isFirst = false;
                        await _jsonSerializer.SerializeAsync(countingPipeWriter, entry.Response, indented: false);
                        _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                        // We reached the limit and don't want to responded to more request in the batch
                        if (!_jsonRpcContext.IsAuthenticated && countingPipeWriter.WrittenCount > _maxBatchResponseBodySize)
                        {
                            enumerator.IsStopped = true;
                        }
                    }
                }

                await countingPipeWriter.WriteAsync(_jsonClosingBracket, cancellationToken);
            }
            else
            {
                await _jsonSerializer.SerializeAsync(countingPipeWriter, result.Response, indented: false);
            }
            await WriteEndOfMessage(countingPipeWriter, cancellationToken);

            long startTime = result.Report?.StartTime ?? 0;
            if (result.IsCollection)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                _ = _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, startTime, true), handlingTimeMicroseconds, countingPipeWriter.WrittenCount);
            }
            else
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                _ = _jsonRpcLocalStats.ReportCall(result.Report!.Value, handlingTimeMicroseconds, countingPipeWriter.WrittenCount);
            }

            IncrementBytesSentMetric((int)countingPipeWriter.WrittenCount);
        }

        await PipeWriter.CompleteAsync();
    }

    protected abstract Task<int> WriteEndOfMessage(CountingPipeWriter pipeWriter, CancellationToken cancellationToken);

    public async Task SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken)
    {
        await _writerChannel.Writer.WriteAsync(result, cancellationToken);
    }

    public event EventHandler? Closed;
    private bool _isDisposed;

    public void Dispose()
    {
        _jsonRpcContext.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
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

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Dispose();

        _readCanceller?.Dispose();
        var delay = Task.Delay(TimeSpan.FromSeconds(1));
        if (await Task.WhenAny(_exitedCompletionSource.Task, delay) == delay)
        {
            // Ahh... not good. Still, can't block dispose
        }
    }
}
