//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public class JsonRpcWebSocketsClient : IWebSocketsClient
    {
        private readonly IWebSocketsClient _client;
        private readonly JsonRpcProcessor _jsonRpcProcessor;
        private readonly JsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        public string Id => _client.Id;
        public string Client { get; }

        public JsonRpcWebSocketsClient(IWebSocketsClient client,
            JsonRpcProcessor jsonRpcProcessor,
            JsonRpcService jsonRpcService, 
            IJsonSerializer jsonSerializer,
            IJsonRpcLocalStats jsonRpcLocalStats)
        {
            _client = client;
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonSerializer = jsonSerializer;
            _jsonRpcLocalStats = jsonRpcLocalStats;
        }

        public async Task ReceiveAsync(Memory<byte> data)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            using JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync(Encoding.UTF8.GetString(data.ToArray()));

            string resultData;
            
            try
            {
                resultData = result.IsCollection ? _jsonSerializer.Serialize(result.Responses) : _jsonSerializer.Serialize(result.Response);
            }
            catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException)
            {
                JsonRpcErrorResponse? error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                resultData = _jsonSerializer.Serialize(error);
            }
            
            await SendRawAsync(resultData);
            
            if (result.IsCollection)
            {
                _jsonRpcLocalStats.ReportCalls(result.Reports);
                _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", stopwatch.ElapsedMicroseconds(), true));
            }
            else
            {
                _jsonRpcLocalStats.ReportCall(result.Report, stopwatch.ElapsedMicroseconds());
            }
        }

        public Task SendRawAsync(string data) => _client.SendRawAsync(data);
        public Task SendAsync(WebSocketsMessage message) => _client.SendAsync(message);
    }
}
