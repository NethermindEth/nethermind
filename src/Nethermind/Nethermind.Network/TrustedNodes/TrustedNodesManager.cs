// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class TrustedNodesManager : ITrustedNodesManager
    {
        private ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();
        private readonly string _trustedNodesPath;
        private readonly ILogger _logger;
        private readonly Channel<Node> _nodeChannel = Channel.CreateBounded<Node>(
        new BoundedChannelOptions(1 << 16)  // capacity of 2^16 = 65536
        {
            // "Wait" to have writers wait until there is space.
            FullMode = BoundedChannelFullMode.Wait
        });

        public TrustedNodesManager(string trustedNodesPath, ILogManager logManager)
        {
            _trustedNodesPath = trustedNodesPath.GetApplicationResourcePath();
            _logger = logManager.GetClassLogger();
        }

        public IEnumerable<NetworkNode> Nodes => _nodes.Values;

        public async Task InitAsync()
        {
            if (!File.Exists(_trustedNodesPath))
            {
                if (_logger.IsDebug) _logger.Debug($"Trusted nodes file not found at: {_trustedNodesPath}");
                return;
            }

            var nodes = new ConcurrentDictionary<PublicKey, NetworkNode>();

            await foreach (string line in File.ReadLinesAsync(_trustedNodesPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    NetworkNode node = new NetworkNode(line);
                    nodes.TryAdd(node.NodeId, node);
                }
                catch (Exception ex) when (ex is ArgumentException or SocketException)
                {
                    if (_logger.IsError)
                    {
                        _logger.Error($"Failed to parse '{line}' as a trusted node.", ex);
                    }
                }
            }

            if (_logger.IsInfo)
            {
                _logger.Info($"Loaded {nodes.Count} trusted nodes from: {Path.GetFullPath(_trustedNodesPath)}");
            }
            if (_logger.IsDebug && !nodes.IsEmpty)
            {
                _logger.Debug("Trusted nodes:\n" + string.Join(Environment.NewLine, nodes.Values.Select(n => n.ToString())));
            }

            _nodes = nodes;
        }


        // ---- INodeSource requirement: IAsyncEnumerable<Node> ----
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
            NetworkNode networkNode = new NetworkNode(enode);
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
            Node newNode = new Node(networkNode) { IsTrusted = true };
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



        // ---- INodeSource requirement: event EventHandler<NodeEventArgs> ----
        public event EventHandler<NodeEventArgs>? NodeRemoved;

        private void OnNodeRemoved(NetworkNode node)
        {
            Node nodeForEvent = new Node(node);
            NodeRemoved?.Invoke(this, new NodeEventArgs(nodeForEvent));
        }


        private async Task SaveFileAsync()
        {
            IEnumerable<string> enodes = _nodes.Values.Select(n => n.ToString());
            using (FileStream stream = File.Create(_trustedNodesPath))
            {
                await JsonSerializer.SerializeAsync(stream, enodes, new JsonSerializerOptions { WriteIndented = true });
            }
        }
    }
}
