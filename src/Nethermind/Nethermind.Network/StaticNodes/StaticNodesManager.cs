// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.StaticNodes;

public class StaticNodesManager(string staticNodesPath, ILogManager logManager) : NodesManager(staticNodesPath, logManager.GetClassLogger()), IStaticNodesManager
{
    public IEnumerable<NetworkNode> Nodes => _nodes.Values;

    public async Task InitAsync()
    {
        ConcurrentDictionary<PublicKey, NetworkNode> nodes = await ParseNodes("static-nodes.json");

        LogNodeList("Static nodes", nodes);

        _nodes = nodes;
    }

    public async Task<bool> AddAsync(string enode, bool updateFile = true)
    {
        NetworkNode networkNode = new(enode);
        if (!_nodes.TryAdd(networkNode.NodeId, networkNode))
        {
            if (_logger.IsInfo) _logger.Info($"Static node was already added: {enode}");
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Static node added: {enode}");

        Node node = new(networkNode);
        NodeAdded?.Invoke(this, new NodeEventArgs(node));

        if (updateFile)
        {
            await SaveFileAsync();
        }

        return true;
    }

    public async Task<bool> RemoveAsync(string enode, bool updateFile = true)
    {
        NetworkNode networkNode = new(enode);
        if (!_nodes.TryRemove(networkNode.NodeId, out _))
        {
            if (_logger.IsInfo) _logger.Info($"Static node was not found: {enode}");
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Static node was removed: {enode}");
        Node node = new(networkNode);
        NodeRemoved?.Invoke(this, new NodeEventArgs(node));
        if (updateFile)
        {
            await SaveFileAsync();
        }

        return true;
    }

    public bool IsStatic(string enode)
    {
        NetworkNode node = new(enode);
        return _nodes.TryGetValue(node.NodeId, out NetworkNode staticNode) && string.Equals(staticNode.Host,
            node.Host, StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<Node> ch = Channel.CreateBounded<Node>(128); // Some reasonably large value

        foreach (Node node in _nodes.Values.Select(n => new Node(n)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return node;
        }

        void handler(object? _, NodeEventArgs args)
        {
            ch.Writer.TryWrite(args.Node);
        }

        try
        {
            NodeAdded += handler;

            await foreach (Node node in ch.Reader.ReadAllAsync(cancellationToken))
            {
                yield return node;
            }
        }
        finally
        {
            NodeAdded -= handler;
        }
    }

    private event EventHandler<NodeEventArgs>? NodeAdded;

    public event EventHandler<NodeEventArgs>? NodeRemoved;
}
