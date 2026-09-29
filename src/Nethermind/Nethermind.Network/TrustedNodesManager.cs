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
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Network;

public class TrustedNodesManager(string trustedNodesPath, ILogManager logManager)
    : NodesManager(trustedNodesPath, logManager.GetClassLogger<TrustedNodesManager>()), ITrustedNodesManager
{
    private readonly Channel<Node> _nodeChannel = Channel.CreateBounded<Node>(
    new BoundedChannelOptions(1 << 16)  // capacity of 2^16 = 65536
    {
        // "Wait" to have writers wait until there is space.
        FullMode = BoundedChannelFullMode.Wait
    });

    public IEnumerable<NetworkNode> Nodes => _nodes.Select(static kvp => kvp.Value);

    public async Task InitAsync()
    {
        ConcurrentDictionary<PublicKey, NetworkNode> nodes = await ParseNodes("trusted-nodes.json");

        LogNodeList("Trusted nodes", nodes);

        SetNodes(nodes);
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // yield existing nodes.
        foreach (KeyValuePair<PublicKey, NetworkNode> kvp in _nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Node(kvp.Value) { IsTrusted = true };
        }

        // yield new nodes as they are added via the channel
        await foreach (Node node in _nodeChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return node;
        }
    }

    public async Task<bool> AddAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default)
    {
        NetworkNode networkNode = new(enode);
        bool added = TryAddNode(networkNode);
        if (_logger.IsInfo) _logger.Info(added ? $"Trusted node added: {enode}" : $"Trusted node was already added: {enode}");

        if (added)
        {
            // Publish the newly added node to the channel so DiscoverNodes will yield it.
            await _nodeChannel.Writer.WriteAsync(new Node(networkNode) { IsTrusted = true }, cancellationToken);
        }

        return await PersistAsync(added, networkNode, updateFile, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Enode enode, bool updateFile = true, CancellationToken cancellationToken = default)
    {
        NetworkNode networkNode = new(enode);
        // TryRemoveNode fires NodeRemoved BEFORE the file write: a cancelled SaveFileAsync must not leave
        // the peer disconnected in-memory but still persisted as trusted.
        bool removed = TryRemoveNode(networkNode.NodeId);
        if (_logger.IsInfo) _logger.Info(removed ? $"Trusted node was removed: {enode}" : $"Trusted node was not found: {enode}");

        return await UnpersistAsync(removed, networkNode, updateFile, cancellationToken);
    }

    public bool IsTrusted(Enode enode) => _nodes.ContainsKey(enode.PublicKey);
}
