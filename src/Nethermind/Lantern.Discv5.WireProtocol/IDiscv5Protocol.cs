using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Table;

namespace Lantern.Discv5.WireProtocol;

public interface IDiscv5Protocol
{
    event Action<NodeTableEntry> NodeAdded;

    event Action<NodeTableEntry> NodeRemoved;

    event Action<NodeTableEntry> NodeAddedToCache;

    event Action<NodeTableEntry> NodeRemovedFromCache;

    int ActiveSessionCount { get; }

    int NodesCount { get; }

    int PeerCount { get; }

    IEnr SelfEnr { get; }

    IEnr? GetEnrForNodeId(byte[] nodeId);

    IEnumerable<IEnr> GetActiveNodes { get; }

    IEnumerable<IEnr> GetAllNodes { get; }

    Task<bool> InitAsync();

    Task<IEnumerable<IEnr>?> DiscoverAsync(byte[] targetNodeId);

    Task<PongMessage?> SendPingAsync(IEnr destination);

    Task<IEnumerable<IEnr>?> SendFindNodeAsync(IEnr destination, byte[] targetNodeId);

    Task<IEnumerable<IEnr>?> SendFindNodeAsync(IEnr destination, int[] distances);

    Task<bool> SendTalkReqAsync(IEnr destination, byte[] protocol, byte[] request);

    bool IsNodeActive(byte[] nodeId);

    Task StopAsync();
}
