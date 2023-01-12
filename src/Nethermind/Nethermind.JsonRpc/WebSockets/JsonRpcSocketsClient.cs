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
        private readonly JsonRpcContext _jsonRpcContext;

        public JsonRpcSocketsClient(
            string clientName,
            ISocketHandler handler,
            RpcEndpoint endpointType,
            IJsonRpcProcessor jsonRpcProcessor,
            IJsonRpcService jsonRpcService,
            IJsonRpcLocalStats jsonRpcLocalStats,
            IJsonSerializer jsonSerializer,
            JsonRpcUrl? url = null)
            : base(clientName, handler, jsonSerializer)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonRpcLocalStats = jsonRpcLocalStats;
            _jsonRpcContext = new JsonRpcContext(endpointType, this, url);
        }

        public override void Dispose()
        {
            base.Dispose();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private static readonly byte[] _jsonOpeningBracket = {Convert.ToByte('{')};
        private static readonly byte[] _jsonComma = {Convert.ToByte(',')};
        private static readonly byte[] _jsonClosingBracket = { Convert.ToByte('}') };

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
                    _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, singleResponseSize);
                }
                else
                {
                    long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                    _jsonRpcLocalStats.ReportCall(result.Response!.Value.Report, handlingTimeMicroseconds, singleResponseSize);
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
            if (result.IsCollection)
            {
                int singleResponseSize = 0;
                bool isFirst = true;
                await _handler.SendRawAsync(_jsonOpeningBracket);
                await foreach (JsonRpcResult.Entry innerResult in result.BatchedResponses!)
                {
                    if (!isFirst) await _handler.SendRawAsync(_jsonComma);
                    isFirst = false;

                    singleResponseSize += await SendJsonRpcResultEntry(innerResult);
                    _jsonRpcLocalStats.ReportCall(innerResult.Report);
                }
                await _handler.SendRawAsync(_jsonClosingBracket);

                return singleResponseSize;
            }
            else
            {
                int singleResponseSize = await SendJsonRpcResultEntry(result.Response.Value);
                return singleResponseSize;
            }
        }

        private async Task<int> SendJsonRpcResultEntry(JsonRpcResult.Entry result)
        {
            void SerializeTimeoutException(MemoryStream stream)
            {
                JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                _jsonSerializer.Serialize(stream, error);
            }

            using (result)
            {
                await using MemoryStream resultData = new();

                try
                {
                    _jsonSerializer.Serialize(resultData, result.Response);
                }
                catch (Exception e) when (e.InnerException is OperationCanceledException)
                {
                    SerializeTimeoutException(resultData);
                }
                catch (OperationCanceledException)
                {
                    SerializeTimeoutException(resultData);
                }

                if (resultData.TryGetBuffer(out ArraySegment<byte> data))
                {
                    await _handler.SendRawAsync(data);
                    return data.Count;
                }

                return (int)resultData.Length;
            }
        }
    }
}
