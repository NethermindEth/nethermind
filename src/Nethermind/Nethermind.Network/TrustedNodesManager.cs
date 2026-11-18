// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network;

public class TrustedNodesManager(string trustedNodesPath, ILogManager logManager)
    : NodesManager(trustedNodesPath, logManager.GetClassLogger<TrustedNodesManager>()), ITrustedNodesManager
{
    public IEnumerable<NetworkNode> Nodes => _nodes.Select(static kvp => kvp.Value);

    public async Task InitAsync()
    {
        ConcurrentDictionary<PublicKey, NetworkNode> nodes = await ParseNodes("trusted-nodes.json");

        LogNodeList("Trusted nodes", nodes);

        SetNodes(nodes);
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (NetworkNode node in DiscoverNetworkNodes(cancellationToken))
        {
            yield return new Node(node) { IsTrusted = true };
        }
    }

    public async Task<bool> AddAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default)
    {
        NetworkNode networkNode = new(enode);
        bool added = TryAddNode(networkNode);
        if (_logger.IsInfo) _logger.Info($"Trusted node {(added ? "added" : "was already added")}: {enode:hide}");

        return await PersistAsync(added, networkNode, updateFile, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default)
    {
        NetworkNode networkNode = new(enode);
        // TryRemoveNode fires NodeRemoved BEFORE the file write: a cancelled SaveFileAsync must not leave
        // the peer disconnected in-memory but still persisted as trusted.
        bool removed = TryRemoveNode(networkNode.NodeId);
        if (_logger.IsInfo) _logger.Info($"Trusted node {(removed ? "was removed" : "was not found")}: {enode:hide}");

        return await UnpersistAsync(removed, networkNode, updateFile, cancellationToken);
    }

    public bool IsTrusted(Enode enode) => _nodes.ContainsKey(enode.PublicKey);
}
