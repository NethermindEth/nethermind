using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Facade.Find
{
    public interface ILogIndexService : IAsyncDisposable, IStoppableService
    {
        Task StartAsync();
        Task BackwardSyncCompletion { get; }
        bool IsRunning { get; }
        int MaxTargetBlockNumber { get; }
        int MinTargetBlockNumber { get; }
        DateTimeOffset? LastUpdate { get; }
        Exception? LastError { get; }
    }
}
