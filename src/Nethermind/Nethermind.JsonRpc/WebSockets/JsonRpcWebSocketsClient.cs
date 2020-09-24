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
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        public string Id => _client.Id;
        public string Client { get; }

        public JsonRpcWebSocketsClient(IWebSocketsClient client,
            JsonRpcProcessor jsonRpcProcessor,
            IJsonSerializer jsonSerializer,
            IJsonRpcLocalStats jsonRpcLocalStats)
        {
            _client = client;
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonSerializer = jsonSerializer;
            _jsonRpcLocalStats = jsonRpcLocalStats;
        }

        public async Task ReceiveAsync(Memory<byte> data)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync(Encoding.UTF8.GetString(data.ToArray()));
            if (result.IsCollection)
            {
                await SendRawAsync(_jsonSerializer.Serialize(result.Responses));
                _jsonRpcLocalStats.ReportCalls(result.Reports);
                _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", stopwatch.ElapsedMicroseconds(), true));
            }
            else
            {
                await SendRawAsync(_jsonSerializer.Serialize(result.Response));
                _jsonRpcLocalStats.ReportCall(result.Report, stopwatch.ElapsedMicroseconds());
            }
        }

        public Task SendRawAsync(string data) => _client.SendRawAsync(data);
        public Task SendAsync(WebSocketsMessage message) => _client.SendAsync(message);
    }
}
