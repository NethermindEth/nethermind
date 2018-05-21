using System.Threading.Tasks;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;

namespace Nethermind.Discovery
{
    public interface IDiscoveryStorage
    {
        (Node Node, long PersistedReputation)[] GetPersistedNodes();
        void PersistNodes(INodeLifecycleManager[] nodes);
        Task PersistNodesAsync(INodeLifecycleManager[] nodes);
    }
}