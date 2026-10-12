// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.LightClient;

/// <summary>Keeps a bounded snapshot of signed discovery records for the next startup.</summary>
internal sealed class BoundedEnrStorage(INetworkStorage storage) : INetworkStorage
{
    internal const int Capacity = 512;

    internal static IFullDb OpenDb(string directory, ILogManager logs, bool beacon = false)
    {
        Nethermind.Logging.ILogger logger = beacon ? logs.GetClassLogger<BeaconPeerTransport>() : logs.GetClassLogger<ExecutionPeerTransport>();
        string network = beacon ? "Beacon" : "Execution";
        string path = Path.Combine(directory, SimpleFilePublicKeyDb.DbFileName);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                string backup = path + ".invalid-" + Guid.NewGuid().ToString("N");
                File.Move(path, backup);
                if (logger.IsWarn) logger.Warn($"{network} discovery cache exceeded its size limit; preserved at {backup} and starting with fresh discovery.");
            }
            return new SimpleFilePublicKeyDb(DbNames.DiscoveryV5Nodes, directory, logs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            string seeds = beacon ? "bootnodes" : "bootnodes and DNS";
            if (logger.IsWarn) logger.Warn($"{network} discovery cache is unavailable; continuing with {seeds}: {exception.Message}");
            return new MemDb(DbNames.DiscoveryV5Nodes);
        }
    }
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
