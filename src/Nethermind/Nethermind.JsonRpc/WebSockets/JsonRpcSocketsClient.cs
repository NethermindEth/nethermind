// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcSocketsClient : SocketClient, IJsonRpcDuplexClient
    {
        public event EventHandler? Closed;

        private readonly IJsonRpcProcessor _jsonRpcProcessor;
        private readonly IJsonRpcService _jsonRpcService;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        private readonly long? _maxBatchResponseBodySize;
        private readonly JsonRpcContext _jsonRpcContext;

        private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

        public JsonRpcSocketsClient(
            string clientName,
            ISocketHandler handler,
            RpcEndpoint endpointType,
            IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcService jsonRpcService,
            IJsonRpcLocalStats jsonRpcLocalStats,
            IJsonSerializer jsonSerializer,
            JsonRpcUrl? url = null,
            long? maxBatchResponseBodySize = null)
            : base(clientName, handler, jsonSerializer)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
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
            using TextReader request = new StreamReader(new MemoryStream(data.Array!, data.Offset, data.Count), Encoding.UTF8);
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
                await using Stream stream = _handler.SendUsingStream();
                await using Stream buffered = new BufferedStream(stream);
                await using CounterStream resultData = new CounterStream(buffered);

                if (result.IsCollection)
                {
                    bool isFirst = true;
                    await resultData.WriteAsync(_jsonOpeningBracket);
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
                                    await resultData.WriteAsync(_jsonComma);
                                }
                                isFirst = false;
                                SendJsonRpcResultEntry(resultData, entry);
                                _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                                // We reached the limit and don't want to responded to more request in the batch
                                if (!_jsonRpcContext.IsAuthenticated && resultData.WrittenBytes > _maxBatchResponseBodySize)
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

                    await resultData.WriteAsync(_jsonClosingBracket);
                }
                else
                {
                    SendJsonRpcResultEntry(resultData, result.SingleResponse!.Value);
                }

                // ? What if we write more than int.MaxValue.
                // Result could be negative
                return (int)resultData.WrittenBytes;
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private void SendJsonRpcResultEntry(Stream dest, JsonRpcResult.Entry result)
        {
            using JsonRpcResult.Entry entry = result;

            try
            {
                _jsonSerializer.SerializeWaitForEnumeration(dest, result.Response);
            }
            catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
            {
                _jsonSerializer.Serialize(
                    dest,
                    _jsonRpcService.GetErrorResponse(
                        ErrorCodes.Timeout,
                        "Request was canceled due to enabled timeout.",
                        result.Response.Id,
                        result.Response.MethodName
                    )
                );
            }
        }
    }
}
