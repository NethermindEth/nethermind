using System.Threading.Tasks;

namespace Nethermind.Grpc.Clients
{
    public interface IGrpcClient
    {
        Task StartAsync();
        Task StopAsync();
        Task PublishAsync<T>(T data);
    }
}