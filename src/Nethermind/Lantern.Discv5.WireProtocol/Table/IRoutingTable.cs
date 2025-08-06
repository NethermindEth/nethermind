using Lantern.Discv5.Enr;

namespace Lantern.Discv5.WireProtocol.Table;

public interface IRoutingTable
{
    event Action<NodeTableEntry> NodeAdded;

    event Action<NodeTableEntry> NodeRemoved;

    event Action<NodeTableEntry> NodeAddedToCache;

    event Action<NodeTableEntry> NodeRemovedFromCache;

    TableOptions TableOptions { get; }

    int GetNodesCount();

    int GetActiveNodesCount();

    IEnumerable<IEnr> GetActiveNodes();

    IEnumerable<IEnr> GetAllNodes();

    NodeTableEntry? GetLeastRecentlySeenNode();

    void UpdateFromEnr(IEnr enr);

    void MarkNodeAsResponded(byte[] nodeId);

    void MarkNodeAsPending(byte[] nodeId);

    void MarkNodeAsLive(byte[] nodeId);

    void MarkNodeAsDead(byte[] nodeId);

    void IncreaseFailureCounter(byte[] nodeId);

    NodeTableEntry? GetNodeEntryForNodeId(byte[] nodeId);

    List<NodeTableEntry> GetClosestNodes(byte[] targetNodeId);

    List<IEnr>? GetEnrRecordsAtDistances(IEnumerable<int> distances);
}