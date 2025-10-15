// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Stats.Model;

namespace Nethermind.Network.StaticNodes;

public class StaticNodesManager(string staticNodesPath, ILogManager logManager) : IStaticNodesManager
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();

    public IEnumerable<NetworkNode> Nodes => _nodes.Values;

    public async Task InitAsync()
    {
        if (!File.Exists(staticNodesPath))
        {
            using Stream embeddedNodes = typeof(StaticNodesManager).Assembly.GetManifestResourceStream("static-nodes.json");

            if (embeddedNodes is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Static nodes resource was not found");
                return;
            }

            // Create the directory if needed
            Directory.CreateDirectory(Path.GetDirectoryName(staticNodesPath));
            using Stream actualNodes = File.Create(staticNodesPath);

            if (_logger.IsTrace) _logger.Trace($"Static nodes file was not found, creating one at {Path.GetFullPath(staticNodesPath)}");

            await embeddedNodes.CopyToAsync(actualNodes);
        }

        string data = await File.ReadAllTextAsync(staticNodesPath);
        ISet<string> nodeSet = INodeSource.ParseNodes(data);

        if (_logger.IsInfo)
            _logger.Info($"Loaded {nodeSet.Count} static nodes from {Path.GetFullPath(staticNodesPath)}");

        if (nodeSet.Count != 0)
        {
            if (_logger.IsDebug) _logger.Debug($"Static nodes: {Environment.NewLine}{data}");
        }

        List<NetworkNode> nodes = [];

        foreach (string? n in nodeSet)
        {
            NetworkNode node;

            try
            {
                node = new(n);
            }
            catch (ArgumentException ex)
            {
                if (_logger.IsError) _logger.Error($"Failed to parse '{n}' as a node", ex);

                continue;
            }

            nodes.Add(node);
        }

        _nodes = new ConcurrentDictionary<PublicKey, NetworkNode>(nodes.ToDictionary(static n => n.NodeId, static n => n));
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

    private Task SaveFileAsync()
        => File.WriteAllTextAsync(staticNodesPath,
            JsonSerializer.Serialize(_nodes.Select(static n => n.Value.ToString()), EthereumJsonSerializer.JsonOptionsIndented));

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
