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
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Grpc.Servers
{
    public class GrpcServer : NethermindService.NethermindServiceBase, IGrpcServer
    {
        private const int MaxCapacity = 1000;
        private readonly IJsonSerializer _jsonSerializer;
        private static readonly QueryResponse EmptyQueryResponse = new QueryResponse();

        private readonly ConcurrentDictionary<string, BlockingCollection<string>> _clientResults =
            new ConcurrentDictionary<string, BlockingCollection<string>>();

        private readonly ILogger _logger;

        public GrpcServer(IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer;
            _logger = logManager.GetClassLogger();
        }

        public override Task<QueryResponse> Query(QueryRequest request, ServerCallContext context)
            => Task.FromResult(EmptyQueryResponse);

        public override async Task Subscribe(SubscriptionRequest request,
            IServerStreamWriter<SubscriptionResponse> responseStream, ServerCallContext context)
        {
            var client = request.Client ?? string.Empty;
            var results = _clientResults.AddOrUpdate(client,
                (_) => new BlockingCollection<string>(MaxCapacity),
                (_, r) => r);

            if (_logger.IsInfo) _logger.Info($"Starting the data stream for client: '{client}', args: {string.Join(", ", request.Args)}.");
            try
            {
                while (true)
                {
                    var result = results.Take();
                    await responseStream.WriteAsync(new SubscriptionResponse
                    {
                        Client = client,
                        Data = result
                    });
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.Message, ex);
            }
            finally
            {
                _clientResults.TryRemove(client, out _);
                if (_logger.IsInfo) _logger.Info($"Finished the data stream for client: '{client}'.");
            }
        }

        public Task PublishAsync<T>(T data, string client) where T : class
        {
            if (data is null)
            {
                return Task.CompletedTask;
            }

            if (_clientResults.Count == 0)
            {
                return Task.CompletedTask;
            }
            
            var payload = _jsonSerializer.Serialize(data);
            if (string.IsNullOrWhiteSpace(client))
            {
                foreach (var (_, results) in _clientResults)
                {
                    try
                    {
                        results.TryAdd(payload);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsError) _logger.Error(ex.Message, ex);
                    }
                }

                return Task.CompletedTask;
            }

            if (!_clientResults.TryGetValue(client, out var clientResult))
            {
                return Task.CompletedTask;
            }

            clientResult.Add(payload);

            return Task.CompletedTask;
        }
    }
}
