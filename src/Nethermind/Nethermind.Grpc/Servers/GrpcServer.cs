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
        private readonly IJsonSerializer _jsonSerializer;
        private static readonly QueryResponse QueryResponse = new QueryResponse();
        private readonly BlockingCollection<string> _results = new BlockingCollection<string>();

        private readonly ILogger _logger;

        public GrpcServer(IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer;
            _logger = logManager.GetClassLogger();
        }

        public override Task<QueryResponse> Query(QueryRequest request, ServerCallContext context)
            => Task.FromResult(QueryResponse);

        public override async Task Subscribe(SubscriptionRequest request,
            IServerStreamWriter<SubscriptionResponse> responseStream, ServerCallContext context)
        {
            try
            {
                while (true)
                {
                    var result = _results.Take();
                    await responseStream.WriteAsync(new SubscriptionResponse
                    {
                        Data = result
                    });
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error(ex.Message, ex);
            }
        }

        public Task PublishAsync<T>(T data) where T : class
        {
            if (data is null)
            {
                return Task.CompletedTask;
            }

            _results.TryAdd(_jsonSerializer.Serialize(data));

            return Task.CompletedTask;
        }
    }
}