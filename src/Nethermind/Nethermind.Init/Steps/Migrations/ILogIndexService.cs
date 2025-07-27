using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Init.Steps.Migrations
{
    public interface ILogIndexService : IAsyncDisposable, IStoppableService
    {
        Task StartAsync();
    }
}
