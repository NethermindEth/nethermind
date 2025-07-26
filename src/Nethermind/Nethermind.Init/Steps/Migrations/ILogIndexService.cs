using System;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps.Migrations
{
    public interface ILogIndexService: IAsyncDisposable
    {
        Task StartAsync();
    }
}
