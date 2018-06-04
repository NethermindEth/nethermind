using System.Threading.Tasks;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;

namespace Nethermind.Discovery
{
    public interface IDiscoveryStorage
    {
        (Node Node, long PersistedReputation)[] GetPersistedNodes();
        void UpdateNodes(INodeLifecycleManager[] nodes);
        void RemoveNodes(INodeLifecycleManager[] nodes);
        void StartBatch();
        void Commit();
        bool AnyPendingChange();
        //void PersistNodes(INodeLifecycleManager[] nodes);
        //Task PersistNodesAsync(INodeLifecycleManager[] nodes);
    }
}