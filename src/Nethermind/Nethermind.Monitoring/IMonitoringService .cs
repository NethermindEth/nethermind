using System.Threading.Tasks;

namespace Nethermind.Monitoring
{
    public interface IMonitoringService
    {
        Task StartAsync();
        Task StopAsync();
    }
}