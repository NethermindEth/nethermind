// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Network.StaticNodes;
using Nethermind.Serialization.Json;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class TrustedNodesManager : ITrustedNodesManager
    {
        private ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();

        private readonly string _trustedNodesPath;
        private readonly ILogger _logger;

        public TrustedNodesManager(string trustedNodesPath, ILogManager logManager)
        {
            _trustedNodesPath = trustedNodesPath.GetApplicationResourcePath();
            _logger = logManager.GetClassLogger();
        }

        public IEnumerable<NetworkNode> Nodes => _nodes.Values;

        private static readonly char[] separator = new[] { '\r', '\n' };

        public async Task InitAsync()
        {
            if (!File.Exists(_trustedNodesPath))
            {
                if (_logger.IsDebug) _logger.Debug($"Trusted nodes file not found for path: {_trustedNodesPath}");
                return;
            }

            string data = await File.ReadAllTextAsync(_trustedNodesPath);
            string[] nodes = GetNodes(data);
            if (_logger.IsInfo)
                _logger.Info($"Loaded {nodes.Length} trusted nodes from file: {Path.GetFullPath(_trustedNodesPath)}");
            if (nodes.Length != 0 && _logger.IsDebug)
            {
                _logger.Debug($"Trusted nodes: {Environment.NewLine}{data}");
            }

            List<NetworkNode> networkNodes = new();
            foreach (string? n in nodes)
            {
                try
                {
                    NetworkNode networkNode = new(n);
                    networkNodes.Add(networkNode);
                }
                catch (Exception exception) when (exception is ArgumentException or SocketException)
                {
                    if (_logger.IsError) _logger.Error("Unable to process node. ", exception);
                }
            }

            _nodes = new ConcurrentDictionary<PublicKey, NetworkNode>(networkNodes.ToDictionary(n => n.NodeId, n => n));
        }

        private static string[] GetNodes(string data)
        {
            string[] nodes;
            try
            {
                nodes = JsonSerializer.Deserialize<string[]>(data) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                nodes = data.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            }

            return nodes.Distinct().ToArray();
        }

        public async Task<bool> AddAsync(string enode, bool updateFile = true)
        {
            NetworkNode networkNode = new(enode);
            if (!_nodes.TryAdd(networkNode.NodeId, networkNode))
            {
                if (_logger.IsInfo) _logger.Info($"Trusted node was already added: {enode}");
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Trusted node added: {enode}");
            Node node = new(networkNode) { IsTrusted = true };
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
                if (_logger.IsInfo) _logger.Info($"Trusted node was not found: {enode}");
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Trusted node was removed: {enode}");
            Node node = new(networkNode) { IsTrusted = true };
            NodeRemoved?.Invoke(this, new NodeEventArgs(node));
            if (updateFile)
            {
                await SaveFileAsync();
            }

            return true;
        }

        public bool IsTrusted(string enode)
        {
            NetworkNode node = new(enode);
            return _nodes.ContainsKey(node.NodeId);
        }

        private Task SaveFileAsync()
            => File.WriteAllTextAsync(_trustedNodesPath,
                JsonSerializer.Serialize(_nodes.Values.Select(n => n.ToString()), EthereumJsonSerializer.JsonOptionsIndented));

        public async IAsyncEnumerable<Node> DiscoverNodes([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Channel<Node> ch = Channel.CreateBounded<Node>(128);

            foreach (Node node in _nodes.Values.Select(n => new Node(n) { IsTrusted = true }))
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
}
