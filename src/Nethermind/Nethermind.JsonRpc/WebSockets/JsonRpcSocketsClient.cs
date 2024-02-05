// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Nethermind.Core.Extensions;
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

    public JsonRpcSocketsClient(
        string clientName,
        TStream stream,
        RpcEndpoint endpointType,
        IJsonRpcProcessor jsonRpcProcessor,
        IJsonRpcLocalStats jsonRpcLocalStats,
        IJsonSerializer jsonSerializer,
        JsonRpcUrl? url = null,
        long? maxBatchResponseBodySize = null)
        : base(clientName, stream, jsonSerializer)
    {
        _jsonRpcProcessor = jsonRpcProcessor;
        _jsonRpcLocalStats = jsonRpcLocalStats;
        _maxBatchResponseBodySize = maxBatchResponseBodySize;
        _jsonRpcContext = new JsonRpcContext(endpointType, this, url);
    }

    public override void Dispose()
    {
        base.Dispose();
        _sendSemaphore.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly byte[] _jsonOpeningBracket = { Convert.ToByte('[') };
    private static readonly byte[] _jsonComma = { Convert.ToByte(',') };
    private static readonly byte[] _jsonClosingBracket = { Convert.ToByte(']') };

    public override async Task ProcessAsync(ArraySegment<byte> data)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IncrementBytesReceivedMetric(data.Count);
        PipeReader request = PipeReader.Create(new MemoryStream(data.Array!, data.Offset, data.Count));
        int allResponsesSize = 0;

        await foreach (JsonRpcResult result in _jsonRpcProcessor.ProcessAsync(request, _jsonRpcContext))
        {
            stopwatch.Restart();

            int singleResponseSize = await SendJsonRpcResult(result);
            allResponsesSize += singleResponseSize;

            if (result.IsCollection)
            {
                long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                _ = _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, singleResponseSize);
            }
            else
            {
                long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                _ = _jsonRpcLocalStats.ReportCall(result.Report!.Value, handlingTimeMicroseconds, singleResponseSize);
            }
            stopwatch.Restart();
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

    public virtual async Task<int> SendJsonRpcResult(JsonRpcResult result)
    {
        await _sendSemaphore.WaitAsync();
        try
        {
            if (result.IsCollection)
            {
                int responseSize = 1;
                bool isFirst = true;
                await _stream.WriteAsync(_jsonOpeningBracket);
                JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses!.GetAsyncEnumerator(CancellationToken.None);
                try
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        JsonRpcResult.Entry entry = enumerator.Current;
                        using (entry)
                        {
                            if (!isFirst)
                            {
                                await _stream.WriteAsync(_jsonComma);
                                responseSize += 1;
                            }
                            isFirst = false;
                            responseSize += (int)await _jsonSerializer.SerializeAsync(_stream, result.Response, indented: false);
                            _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                            // We reached the limit and don't want to responded to more request in the batch
                            if (!_jsonRpcContext.IsAuthenticated && responseSize > _maxBatchResponseBodySize)
                            {
                                enumerator.IsStopped = true;
                            }
                        }
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }

                await _stream.WriteAsync(_jsonClosingBracket);
                responseSize++;

                responseSize += await _stream.WriteEndOfMessageAsync();

                return responseSize;
            }
            else
            {
                int responseSize = (int)await _jsonSerializer.SerializeAsync(_stream, result.Response, indented: false);
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
