using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules.Data
{
    public interface IDataBridge
    {
        void ReplayBlocks(long startBlockNumber, long endBlockNumber);
        void Start();
        Task StopAsync();
    }
}