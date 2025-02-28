// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets;

public class PipelinesJsonRpcAdapter : PipelineSocketClient, IJsonRpcDuplexClient
{
    private readonly ISocketHandler _socketHandler;
    private readonly IJsonRpcProcessor _jsonRpcProcessor;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
    private readonly JsonRpcContext _jsonRpcContext;
    private readonly ILogger _logger;
    private readonly long? _maxBatchResponseBodySize;
    private readonly long _maxJsonPayloadSize;

    private static readonly byte[] _jsonOpeningBracket = [Convert.ToByte('[')];
    private static readonly byte[] _jsonComma = [Convert.ToByte(',')];
    private static readonly byte[] _jsonClosingBracket = [Convert.ToByte(']')];
    private CancellationTokenSource? _readCanceller;
    private TaskCompletionSource _exitedCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _processConcurrency = 1;

    private readonly Channel<JsonParseResult> _readerChannel = Channel.CreateBounded<JsonParseResult>(new BoundedChannelOptions(1)
    {
        SingleWriter = true
    });

    public record Options(
        long? MaxBatchResponseBodySize = null,
        int ProcessConcurrency = 1,
        long MaxJsonPayloadSize = long.MaxValue
    );

    public PipelinesJsonRpcAdapter(
        string clientName,
        ISocketHandler socketHandler,
        RpcEndpoint endpointType,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        Options options,
        ILogManager logManager,
        JsonRpcUrl? url = null)
    : base(clientName, socketHandler, jsonSerializer)
    {
        _socketHandler = socketHandler;
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _jsonSerializer = jsonSerializer;
        _jsonRpcContext = new JsonRpcContext(endpointType, this, url);
        _logger = logManager.GetClassLogger<PipelinesJsonRpcAdapter>();

        _maxBatchResponseBodySize = options.MaxBatchResponseBodySize;
        _processConcurrency = options.ProcessConcurrency;
        _maxJsonPayloadSize = options.MaxJsonPayloadSize;
    }

    public override async Task Loop(CancellationToken cancellationToken)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        // Cancellation token specifically for read so that we can cancel just the read tasks which finishes
        // the channels.
        _readCanceller = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        Task readTask = ReadLoop(_readCanceller.Token);
        Task processTask = ProcessLoop(cts.Token);
        Task parentTask = base.Loop(cts.Token);

        try
        {
            // Must await so that cts is not cancelled
            await Task.WhenAll(readTask, processTask, parentTask);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _exitedCompletionSource.TrySetResult();
        }
    }

    protected async Task ReadLoop(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var jsonParseResult in JsonRpcUtils.MultiParseJsonDocument(_socketHandler.PipeReader,
                               cancellationToken,
                               maxBufferSize: _maxJsonPayloadSize))
            {
                IncrementBytesReceivedMetric((int)jsonParseResult.ReadSize);
                await _readerChannel.Writer.WriteAsync(jsonParseResult, cancellationToken);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Error in read worker {e}");
            throw;
        }
        finally
        {
            if (_logger.IsTrace) _logger.Trace($"Read loop complete");
            _readerChannel.Writer.Complete();
        }
    }

    private async Task ProcessLoop(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(Enumerable.Range(0, _processConcurrency)
                .Select(async (_) => { await ProcessTaskWorker(cancellationToken); }));
        }
        finally
        {
            if (_logger.IsTrace) _logger.Trace($"Process loop complete");
            _writerChannel.Writer.Complete();
        }
    }

    private async Task ProcessTaskWorker(CancellationToken cancellationToken)
    {
        // TODO: When an exception happen, readerchannel need to stop so that other task stops
        await foreach (var jsonParseResult in _readerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            JsonRpcResult? result = await _jsonRpcProcessor.HandleJsonParseResult(jsonParseResult, _jsonRpcContext, cancellationToken);
            if (result is not null)
            {
                await SendJsonRpcResult(result.Value, cancellationToken);
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("Triggering stop due to null json rpc result");
                // Cancel read
                _readCanceller?.Cancel();
            }
        }
    }

    protected override async Task WriteResult(CountingPipeWriter countingPipeWriter, object result, CancellationToken cancellationToken)
    {
        if (result is not JsonRpcResult jsonRpcResult)
        {
            await base.WriteResult(countingPipeWriter, result, cancellationToken);
        }
        else
        {
            if (jsonRpcResult.IsCollection)
            {
                bool isFirst = true;
                await countingPipeWriter.WriteAsync(_jsonOpeningBracket, cancellationToken);
                await using JsonRpcBatchResultAsyncEnumerator enumerator =
                    jsonRpcResult.BatchedResponses!.GetAsyncEnumerator(CancellationToken.None);
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
                        if (!_jsonRpcContext.IsAuthenticated &&
                            countingPipeWriter.WrittenCount > _maxBatchResponseBodySize)
                        {
                            enumerator.IsStopped = true;
                        }
                    }
                }

                await countingPipeWriter.WriteAsync(_jsonClosingBracket, cancellationToken);
            }
            else
            {
                await _jsonSerializer.SerializeAsync(countingPipeWriter, jsonRpcResult.Response, indented: false);
            }

            long startTime = jsonRpcResult.Report?.StartTime ?? 0;
            if (jsonRpcResult.IsCollection)
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                _ = _jsonRpcLocalStats.ReportCall(
                    new RpcReport("# collection serialization #", handlingTimeMicroseconds, startTime, true),
                    handlingTimeMicroseconds, countingPipeWriter.WrittenCount);
            }
            else
            {
                long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
                _ = _jsonRpcLocalStats.ReportCall(jsonRpcResult.Report!.Value, handlingTimeMicroseconds,
                    countingPipeWriter.WrittenCount);
            }
        }

        IncrementBytesSentMetric((int)countingPipeWriter.WrittenCount);
    }

    public async Task SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken)
    {
        await _writerChannel.Writer.WriteAsync(result, cancellationToken);
    }

    public event EventHandler? Closed;
    private bool _isDisposed;

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

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Just cancel read first, give some time for the rest of the pipeliene to process.
        _readCanceller?.Cancel();
        var delay = Task.Delay(TimeSpan.FromSeconds(1));
        if (await Task.WhenAny(_exitedCompletionSource.Task, delay) == delay)
        {
            if (_logger.IsDebug) _logger.Debug("Unable to stop pipeline cleanly");
            // Ahh... not good. Still, can't block dispose
        }

        _readCanceller?.Dispose();
        await base.DisposeAsync();
        _jsonRpcContext.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);

    }
}
