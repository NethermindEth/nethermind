using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol;

public class Discv5Protocol(IConnectionManager connectionManager,
    IIdentityManager identityManager,
    ITableManager tableManager,
    IRequestManager requestManager,
    IPacketReceiver packetReceiver,
    IPacketManager packetManager,
    IRoutingTable routingTable,
    ISessionManager sessionManager,
    ILookupManager lookupManager,
    ILogger<IDiscv5Protocol> logger) : IDiscv5Protocol
{
    public int NodesCount => routingTable.GetNodesCount();

    public int PeerCount => routingTable.GetActiveNodesCount();

    public int ActiveSessionCount => sessionManager.TotalSessionCount;

    public IEnr SelfEnr => identityManager.Record;

    public IEnr? GetEnrForNodeId(byte[] nodeId)
    {
        var entry = routingTable.GetNodeEntryForNodeId(nodeId);

        return entry?.Record;
    }

    public IEnumerable<IEnr> GetAllNodes => routingTable.GetAllNodes();

    public IEnumerable<IEnr> GetActiveNodes => routingTable.GetActiveNodes();

    public event Action<NodeTableEntry> NodeAdded
    {
        add => routingTable.NodeAdded += value;
        remove => routingTable.NodeAdded -= value;
    }

    public event Action<NodeTableEntry> NodeRemoved
    {
        add => routingTable.NodeRemoved += value;
        remove => routingTable.NodeRemoved -= value;
    }

    public event Action<NodeTableEntry> NodeAddedToCache
    {
        add => routingTable.NodeAddedToCache += value;
        remove => routingTable.NodeAddedToCache -= value;
    }

    public event Action<NodeTableEntry> NodeRemovedFromCache
    {
        add => routingTable.NodeRemovedFromCache += value;
        remove => routingTable.NodeRemovedFromCache -= value;
    }

    public async Task<bool> InitAsync()
    {
        try
        {
            connectionManager.InitAsync();
            requestManager.InitAsync();
            await tableManager.InitAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred in InitAsync. Cannot initialize Discv5 protocol");
            return false;
        }
    }

    public async Task<IEnumerable<IEnr>?> DiscoverAsync(byte[] targetNodeId)
    {
        if (routingTable.GetActiveNodesCount() <= 0)
            return null;

        var closestNodes = await lookupManager.LookupAsync(targetNodeId);

        return closestNodes;
    }

    public async Task<PongMessage?> SendPingAsync(IEnr destination)
    {
        try
        {
            var response = await packetReceiver.SendPingAsync(destination);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred in SendPingAsync. Cannot send PING to {Record}", destination);
            return null;
        }
    }

    public async Task<IEnumerable<IEnr>?> SendFindNodeAsync(IEnr destination, byte[] targetNodeId)
    {
        try
        {
            var response = await packetReceiver.SendFindNodeAsync(destination, targetNodeId);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred in SendFindNodeAsync. Cannot send FINDNODE to {Record}", destination);
            return null;
        }
    }

    public async Task<IEnumerable<IEnr>?> SendFindNodeAsync(IEnr destination, int[] distances)
    {
        try
        {
            var response = await packetReceiver.SendFindNodeAsync(destination, distances);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred in SendFindNodeAsync. Cannot send FINDNODE to {Record}", destination);
            return null;
        }
    }

    public async Task<bool> SendTalkReqAsync(IEnr destination, byte[] protocol, byte[] request)
    {
        try
        {
            await packetManager.SendPacket(destination, MessageType.TalkReq, false, protocol, request);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred in SendTalkReqAsync. Cannot send TALKREQ to {Record}", destination);
            return false;
        }
    }

    public bool IsNodeActive(byte[] nodeId)
    {
        var nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);

        return nodeEntry?.Status == NodeStatus.Live;
    }

    public async Task StopAsync()
    {
        var stopConnectionManagerTask = connectionManager.StopConnectionManagerAsync();
        var stopTableManagerTask = tableManager.StopTableManagerAsync();
        var stopRequestManagerTask = requestManager.StopRequestManagerAsync();

        await Task.WhenAll(stopConnectionManagerTask, stopTableManagerTask, stopRequestManagerTask).ConfigureAwait(false);
    }
}
