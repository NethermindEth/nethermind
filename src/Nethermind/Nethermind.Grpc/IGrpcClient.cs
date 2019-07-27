using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Grpc
{
    public interface IGrpcClient
    {
        Task StartAsync();
        Task StopAsync();
        Task<string> QueryAsync(IEnumerable<string> args);
        Task SubscribeAsync(Action<string> callback, Func<bool> enabled, IEnumerable<string> args);
    }
}