// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// So, because of how the session works, if the routing table does not record the Enr because its full,
/// it does not store the session at all which makes TalkReq to other nodes to not work.
/// </summary>
public class TransientRoutingTable(
    IIdentityManager identityManager,
    IEnrFactory enrFactory,
    ILoggerFactory loggerFactory,
    TableOptions options)
    : IRoutingTable
{

    private IRoutingTable _base = new Lantern.Discv5.WireProtocol.Table.RoutingTable(identityManager, enrFactory, loggerFactory, options);
    private SpanLruCache<byte, NodeTableEntry?> _tableEntryLru = new SpanLruCache<byte, NodeTableEntry?>(
        16000,
        16,
        "node table entry cache",
        Bytes.SpanEqualityComparer
    );

    public NodeTableEntry? GetNodeEntryForNodeId(byte[] nodeId)
    {
        NodeTableEntry? resp = _base.GetNodeEntryForNodeId(nodeId);
        if (resp == null) _tableEntryLru.TryGet(nodeId, out resp);
        return resp;
    }

    public void UpdateFromEnr(IEnr enr)
    {
        _base.UpdateFromEnr(enr);

        if (_tableEntryLru.TryGet(enr.NodeId, out NodeTableEntry? value))
        {
            if (value!.Record.SequenceNumber > enr.SequenceNumber)
            {
                return;
            }
        }

        _tableEntryLru.Set(enr.NodeId, new NodeTableEntry(enr, identityManager.Verifier));
    }

    #region Delegate to base

    public int GetNodesCount()
    {
        return _base.GetNodesCount();
    }

    public int GetActiveNodesCount()
    {
        return _base.GetActiveNodesCount();
    }

    public IEnumerable<IEnr> GetActiveNodes()
    {
        return _base.GetActiveNodes();
    }

    public IEnumerable<IEnr> GetAllNodes()
    {
        return _base.GetAllNodes();
    }

    public NodeTableEntry? GetLeastRecentlySeenNode()
    {
        return _base.GetLeastRecentlySeenNode();
    }

    public void MarkNodeAsResponded(byte[] nodeId)
    {
        _base.MarkNodeAsResponded(nodeId);
    }

    public void MarkNodeAsPending(byte[] nodeId)
    {
        _base.MarkNodeAsPending(nodeId);
    }

    public void MarkNodeAsLive(byte[] nodeId)
    {
        _base.MarkNodeAsLive(nodeId);
    }

    public void MarkNodeAsDead(byte[] nodeId)
    {
        _base.MarkNodeAsDead(nodeId);
    }

    public void IncreaseFailureCounter(byte[] nodeId)
    {
        _base.IncreaseFailureCounter(nodeId);
    }

    public List<NodeTableEntry> GetClosestNodes(byte[] targetNodeId)
    {
        return _base.GetClosestNodes(targetNodeId);
    }

    public List<IEnr>? GetEnrRecordsAtDistances(IEnumerable<int> distances)
    {
        return _base.GetEnrRecordsAtDistances(distances);
    }

    public TableOptions TableOptions => _base.TableOptions;

    public event Action<NodeTableEntry>? NodeAdded
    {
        add => _base.NodeAdded += value;
        remove => _base.NodeAdded -= value;
    }

    public event Action<NodeTableEntry>? NodeRemoved
    {
        add => _base.NodeRemoved += value;
        remove => _base.NodeRemoved -= value;
    }

    public event Action<NodeTableEntry>? NodeAddedToCache
    {
        add => _base.NodeAddedToCache += value;
        remove => _base.NodeAddedToCache -= value;
    }

    public event Action<NodeTableEntry>? NodeRemovedFromCache
    {
        add => _base.NodeRemovedFromCache += value;
        remove => _base.NodeRemovedFromCache -= value;
    }

    #endregion

}
