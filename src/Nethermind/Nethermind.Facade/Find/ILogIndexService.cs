using System;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Facade.Find
{
    public interface ILogIndexService : IAsyncDisposable, IStoppableService
    {
        Task StartAsync();
    }
}
