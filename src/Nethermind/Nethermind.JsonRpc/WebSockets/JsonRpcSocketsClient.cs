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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
            {
                using TextReader request = new StreamReader(new MemoryStream(segment.Array!), Encoding.UTF8);
                int size = 0;
                await foreach (JsonRpcResult result in _jsonRpcProcessor.ProcessAsync(request, _jsonRpcContext))
                {
                    using (result)
                    {
                        size += await SendJsonRpcResult(result);
                        if (result.IsCollection)
                        {
                            _jsonRpcLocalStats.ReportCalls(result.Reports);

                            long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                            _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", handlingTimeMicroseconds, true), handlingTimeMicroseconds, size);
                        }
                        else
                        {
                            long handlingTimeMicroseconds = stopwatch.ElapsedMicroseconds();
                            _jsonRpcLocalStats.ReportCall(result.Report, handlingTimeMicroseconds, size);
                        }
                    }
                }
                
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
            void SerializeTimeoutException(MemoryStream stream)
            {
                JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                _jsonSerializer.Serialize(stream, error);
            }

            await using MemoryStream resultData = new();

            try
            {
                if (result.IsCollection)
                {
                    _jsonSerializer.Serialize(resultData, result.Responses);
                }
                else
                {
                    _jsonSerializer.Serialize(resultData, result.Response);
                }
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
