using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Grpc.Servers
{
    public class GrpcServer : NethermindService.NethermindServiceBase, IGrpcServer
    {
        private const int MaxCapacity = 100000;
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
        }

        public Task PublishAsync<T>(T data, string client) where T : class
        {
            if (data is null)
            {
                return Task.CompletedTask;
            }

            var payload = _jsonSerializer.Serialize(data);
            _clientResults.AddOrUpdate(client ?? string.Empty, (_) =>
            {
                var results = new BlockingCollection<string>(MaxCapacity);
                results.TryAdd(payload);

                return results;
            }, (c, results) =>
            {
                results.TryAdd(payload);

                return results;
            });

            return Task.CompletedTask;
        }
    }
}