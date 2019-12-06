using System.Threading.Tasks;

namespace Nethermind.BeaconNode.Services
{
    public class NodeStart : INodeStart
    {
        public Task InitializeNodeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
