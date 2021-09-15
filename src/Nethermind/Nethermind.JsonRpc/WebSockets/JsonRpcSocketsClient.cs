//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcSocketsClient : SocketClient, IJsonRpcDuplexClient
    {
        public event EventHandler Closed;

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
            IJsonSerializer jsonSerializer)
            : base(clientName, handler, jsonSerializer)
        {
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonRpcLocalStats = jsonRpcLocalStats;
            _jsonRpcContext = new JsonRpcContext(endpointType, this);
        }

        public override void Dispose()
        {
            base.Dispose();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public override async Task ProcessAsync(Memory<byte> data)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            IncrementBytesReceivedMetric(data.Length);
            var responses = await _jsonRpcProcessor.ProcessAsync(Encoding.UTF8.GetString(data.Span), _jsonRpcContext);

            if (responses.Count == 1 && !responses[0].IsCollection)
            {
                using var result = responses[0];
                var size = await SendJsonRpcResult(result);
                long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                
                _jsonRpcLocalStats.ReportCall(result.Report, handlingTimeMicroseconds, size);
                
                IncrementBytesSentMetric(size);
            }
            else if (responses.Count > 0)   // multiple responses or one array response
            {
                int size = 0;
                foreach (var result in responses)
                {
                    using (result)
                    {
                        size += await SendJsonRpcResult(result);
                        
                        _jsonRpcLocalStats.ReportCalls(result.Reports);
                    }
                }
                
                long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, size);
                
                IncrementBytesSentMetric(size);
            }
        }

        private void IncrementBytesReceivedMetric(int size)
        {
            if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.WebSocket)
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
            if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.WebSocket)
            {
                Interlocked.Add(ref Metrics.JsonRpcBytesSentWebSockets, size);
            }

            if (_jsonRpcContext.RpcEndpoint == RpcEndpoint.IPC)
            {
                Interlocked.Add(ref Metrics.JsonRpcBytesSentIpc, size);
            }
        }

        public async Task<int> SendJsonRpcResult(JsonRpcResult result)
        {
            string SerializeTimeoutException()
            {
                JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                return _jsonSerializer.Serialize(error);
            }

            string resultData;

            try
            {
                resultData = result.IsCollection ? _jsonSerializer.Serialize(result.Responses) : _jsonSerializer.Serialize(result.Response);
            }
            catch (Exception e) when (e.InnerException is OperationCanceledException)
            {
                resultData = SerializeTimeoutException();
            }
            catch (OperationCanceledException)
            {
                resultData = SerializeTimeoutException();
            }
            
            await _handler.SendRawAsync(resultData);

            return resultData.Length;
        }
    }
}
