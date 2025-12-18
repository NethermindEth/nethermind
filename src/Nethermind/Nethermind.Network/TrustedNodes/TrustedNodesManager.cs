// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Network;

public class TrustedNodesManager(string trustedNodesPath, ILogManager logManager)
    : NodesManager(trustedNodesPath, logManager.GetClassLogger()), ITrustedNodesManager
{
    private readonly Channel<Node> _nodeChannel = Channel.CreateBounded<Node>(
    new BoundedChannelOptions(1 << 16)  // capacity of 2^16 = 65536
    {
        // "Wait" to have writers wait until there is space.
        FullMode = BoundedChannelFullMode.Wait
    });

    public IEnumerable<NetworkNode> Nodes => _nodes.Values;

    public async Task InitAsync()
    {
        ConcurrentDictionary<PublicKey, NetworkNode> nodes = await ParseNodes("trusted-nodes.json");

        LogNodeList("Trusted nodes", nodes);

        _nodes = nodes;
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // yield existing nodes.
        foreach (NetworkNode netNode in _nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Node(netNode) { IsTrusted = true };
        }

        // yield new nodes as they are added via the channel
        await foreach (Node node in _nodeChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return node;
        }
    }

    public async Task<bool> AddAsync(Enode enode, bool updateFile = true)
    {
        NetworkNode networkNode = new(enode);
        if (!_nodes.TryAdd(networkNode.NodeId, networkNode))
        {
            if (_logger.IsInfo)
            {
                _logger.Info($"Trusted node was already added: {enode}");
            }
            return false;
        }

        if (_logger.IsInfo)
        {
            _logger.Info($"Trusted node added: {enode}");
        }

        // Publish the newly added node to the channel so DiscoverNodes will yield it.
        Node newNode = new(networkNode) { IsTrusted = true };
        await _nodeChannel.Writer.WriteAsync(newNode);

        if (updateFile)
        {
            await SaveFileAsync();
        }
        return true;
    }

    public async Task<bool> RemoveAsync(Enode enode, bool updateFile = true)
    {
        NetworkNode networkNode = new(enode.ToString());
        if (!_nodes.TryRemove(networkNode.NodeId, out _))
        {
            if (_logger.IsInfo)
            {
                _logger.Info($"Trusted node was not found: {enode}");
            }
            return false;
        }

        if (_logger.IsInfo)
        {
            _logger.Info($"Trusted node was removed: {enode}");
        }

        if (updateFile)
        {
            await SaveFileAsync();
        }

        OnNodeRemoved(networkNode);

        return true;
    }

    public bool IsTrusted(Enode enode)
    {
        if (enode.PublicKey is null)
        {
            return false;
        }
        if (_nodes.TryGetValue(enode.PublicKey, out NetworkNode storedNode))
        {
            // Compare not only the public key, but also the host and port.
            return storedNode.Host == enode.HostIp?.ToString() && storedNode.Port == enode.Port;
        }
        return false;
    }

    public event EventHandler<NodeEventArgs>? NodeRemoved;

    private void OnNodeRemoved(NetworkNode node)
    {
        Node nodeForEvent = new(node);
        NodeRemoved?.Invoke(this, new NodeEventArgs(nodeForEvent));
    }
}
