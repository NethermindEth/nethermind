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
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Serialization.Json;
using Nethermind.WebSockets;

namespace Nethermind.JsonRpc.WebSockets
{
    public interface IJsonRpcDuplexClient : IDisposable
    {
        string Id { get; }
        Task SendJsonRpcResult(JsonRpcResult result);
    }

    public class JsonRpcWebSocketsClient : IWebSocketsClient, IJsonRpcDuplexClient
    {
        private readonly IWebSocketsClient _client;
        private readonly JsonRpcProcessor _jsonRpcProcessor;
        private readonly JsonRpcService _jsonRpcService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcLocalStats _jsonRpcLocalStats;
        private readonly ISubscriptionManger _subscriptionManager;
        public string Id => _client.Id;
        public string Client { get; }

        public JsonRpcWebSocketsClient(IWebSocketsClient client,
            JsonRpcProcessor jsonRpcProcessor,
            JsonRpcService jsonRpcService, 
            IJsonSerializer jsonSerializer,
            IJsonRpcLocalStats jsonRpcLocalStats,
            ISubscriptionManger subscriptionManger)
        {
            _client = client;
            _jsonRpcProcessor = jsonRpcProcessor;
            _jsonRpcService = jsonRpcService;
            _jsonSerializer = jsonSerializer;
            _jsonRpcLocalStats = jsonRpcLocalStats;
            _subscriptionManager = subscriptionManger;
        }

        public async Task ReceiveAsync(Memory<byte> data)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            using JsonRpcResult result = await _jsonRpcProcessor.ProcessAsync(Encoding.UTF8.GetString(data.Span), RpcEndpoint.WebSocket);

            await SendJsonRpcResult(result);

            if (result.IsCollection)
            {
                for (int i = 0; i < result.Responses.Count; i++)
                {
                    TrySetupSubscription(result.Responses[i]);
                }
                _jsonRpcLocalStats.ReportCalls(result.Reports);
                _jsonRpcLocalStats.ReportCall(new RpcReport("# collection serialization #", stopwatch.ElapsedMicroseconds(), true));
            }
            else
            {
                TrySetupSubscription(result.Response);
                _jsonRpcLocalStats.ReportCall(result.Report, stopwatch.ElapsedMicroseconds());
            }
        }

        public async Task SendJsonRpcResult(JsonRpcResult result)
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
            
            await SendRawAsync(resultData);
        }

        private void TrySetupSubscription(JsonRpcResponse response)
        {
            if (string.Equals(response.MethodName, "eth_subscribe", StringComparison.InvariantCulture))
            {
                if (response is JsonRpcSuccessResponse successResponse)
                {
                    _subscriptionManager.BindJsonRpcDuplexClient((string)successResponse.Result, this);
                }
            }
        }

        public void Dispose()
        {
            _subscriptionManager.RemoveSubscriptions(this);
        }

        public Task SendRawAsync(string data) => _client.SendRawAsync(data);
        public Task SendAsync(WebSocketsMessage message) => _client.SendAsync(message);
    }
}
