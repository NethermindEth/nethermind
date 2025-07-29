using System;
using System.Threading.Tasks;

namespace Nethermind.Facade.Find
{
    public interface ILogIndexService : IAsyncDisposable
    {
        string Description { get; }
        Task StartAsync();
        Task StopAsync();
    }
}
