using System.Threading.Tasks;
using Nethermind.Blockchain;

namespace Nethermind.Grpc.Producers
{
    public class GrpcProducer : IProducer
    {
        private readonly IGrpcServer _server;

        public GrpcProducer(IGrpcServer server)
        {
            _server = server;
        }

        public Task InitAsync() => Task.CompletedTask;

        public Task PublishAsync<T>(T data) where T : class => _server.PublishAsync(data);

        public Task CloseAsync() => Task.CompletedTask;
    }
}