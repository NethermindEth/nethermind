using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Logging;

namespace Nethermind.Grpc.Clients
{
    public class GrpcProducer : IProducer
    {
        private readonly IGrpcClient _client;
        private readonly ILogManager _logManager;

        public GrpcProducer(IGrpcClient client, ILogManager logManager)
        {
            _client = client;
            _logManager = logManager;
        }
        
        public Task InitAsync() => Task.CompletedTask;

        public Task PublishAsync<T>(T data) where T : class => _client.PublishAsync(data);

        public Task CloseAsync() => Task.CompletedTask;
    }
}