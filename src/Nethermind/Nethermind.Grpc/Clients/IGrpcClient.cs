using System;
using System.Threading.Tasks;

namespace Nethermind.Grpc.Clients
{
    public interface IGrpcClient
    {
        Task StartAsync();
        Task StopAsync();
        Task<string> QueryAsync(params string[] args);
        Task SubscribeAsync(Action<string> callback, params string[] args);
    }
}