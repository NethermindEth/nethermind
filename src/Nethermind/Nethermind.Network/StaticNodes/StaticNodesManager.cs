// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Newtonsoft.Json;

namespace Nethermind.Network.StaticNodes
{
    public class StaticNodesManager : IStaticNodesManager
    {
        private ConcurrentDictionary<PublicKey, NetworkNode> _nodes = new();

        private readonly string _staticNodesPath;
        private readonly ILogger _logger;

        public StaticNodesManager(string staticNodesPath, ILogManager logManager)
        {
            _staticNodesPath = staticNodesPath.GetApplicationResourcePath();
            _logger = logManager.GetClassLogger();
        }

        public IEnumerable<NetworkNode> Nodes => _nodes.Values;

        public async Task InitAsync()
        {
            if (!File.Exists(_staticNodesPath))
            {
                if (_logger.IsDebug) _logger.Debug($"Static nodes file was not found for path: {_staticNodesPath}");

                return;
            }

            string data = await File.ReadAllTextAsync(_staticNodesPath);
            string[] nodes = GetNodes(data);
            if (_logger.IsInfo)
                _logger.Info($"Loaded {nodes.Length} static nodes from file: {Path.GetFullPath(_staticNodesPath)}");
            if (nodes.Length != 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Static nodes: {Environment.NewLine}{data}");
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
                nodes = JsonConvert.DeserializeObject<string[]>(data) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                nodes = data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }

            return nodes.Distinct().ToArray();
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
                node.Host, StringComparison.InvariantCultureIgnoreCase);
        }

        private Task SaveFileAsync()
            => File.WriteAllTextAsync(_staticNodesPath,
                JsonConvert.SerializeObject(_nodes.Select(n => n.Value.ToString()), Formatting.Indented));

        public List<Node> LoadInitialList()
        {
            return _nodes.Values.Select(n => new Node(n)).ToList();
        }

        public event EventHandler<NodeEventArgs>? NodeAdded;

        public event EventHandler<NodeEventArgs>? NodeRemoved;
    }
}
