using System.Threading.Tasks;

namespace Nethermind.Init.Steps.Migrations
{
    public interface ILogIndexService
    {
        Task StartAsync();
        Task StopAsync();
    }
}
