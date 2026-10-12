// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Network;

namespace Nethermind.LightClient;

/// <summary>Keeps a bounded snapshot of signed discovery records for the next startup.</summary>
internal sealed class BoundedEnrStorage(INetworkStorage storage) : INetworkStorage
{
    internal const int Capacity = 512;
    public NetworkNode[] GetPersistedNodes()
    {
        NetworkNode[] nodes = storage.GetPersistedNodes();
        return nodes.Length <= Capacity ? nodes : nodes.AsSpan(0, Capacity).ToArray();
    }
    public int PersistedNodesCount => Math.Min(Capacity, storage.PersistedNodesCount);
    public void StartBatch() => storage.StartBatch();
    public void Commit() => storage.Commit();
    public bool AnyPendingChange() => storage.AnyPendingChange();
    public void RemoveNode(PublicKey nodeId) => storage.RemoveNode(nodeId);
    public void UpdateNode(NetworkNode node)
    {
        if (!node.IsEnr) return;
        NetworkNode[] previous = storage.GetPersistedNodes();
        bool known = false;
        foreach (NetworkNode entry in previous) known |= entry.NodeId == node.NodeId;
        if (!known && previous.Length >= Capacity) storage.RemoveNode(previous[0].NodeId);
        storage.UpdateNode(node);
    }
    public void UpdateNodes(IEnumerable<NetworkNode> nodes)
    {
        Dictionary<PublicKey, NetworkNode> current = [];
        foreach (NetworkNode node in nodes)
        {
            if (!node.IsEnr) continue;
            current[node.NodeId] = node;
            if (current.Count == Capacity) break;
        }
        foreach (NetworkNode previous in storage.GetPersistedNodes())
            if (!current.ContainsKey(previous.NodeId)) storage.RemoveNode(previous.NodeId);
        storage.UpdateNodes(current.Values);
    }
}
