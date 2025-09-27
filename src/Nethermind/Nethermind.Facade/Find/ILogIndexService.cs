using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Facade.Find
{
    public interface ILogIndexService : IAsyncDisposable, IStoppableService
    {
        Task StartAsync();

        bool IsRunning { get; }
        int MaxTargetBlockNumber { get; }
        int MinTargetBlockNumber { get; }
        DateTime? LastUpdateUtc { get; }
        Exception? LastError { get; }
    }
}
