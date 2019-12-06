using System.Threading.Tasks;

namespace Cortex.BeaconNode.Services
{
    public class NodeStart : INodeStart
    {
        public Task InitializeNodeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
