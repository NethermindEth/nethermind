/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.Network.StaticNodes
{
    public class StaticNodesManager : IStaticNodesManager
    {
        private ConcurrentDictionary<PublicKey, NetworkNode> _nodes =
            new ConcurrentDictionary<PublicKey, NetworkNode>();

        private readonly string _staticNodesPath;
        private readonly ILogger _logger;

        public StaticNodesManager(string staticNodesPath, ILogManager logManager)
        {
            _staticNodesPath = staticNodesPath;
            _logger = logManager.GetClassLogger();
        }

        public IEnumerable<NetworkNode> Nodes => _nodes.Values;
        public event EventHandler<NetworkNodeEventArgs> NodeAdded;
        public event EventHandler<NetworkNodeEventArgs> NodeRemoved;

        public async Task InitAsync()
        {
            if (!File.Exists(_staticNodesPath))
            {
                if (_logger.IsDebug) _logger.Debug($"Static nodes file was not found for path: {_staticNodesPath}");

                return;
            }

            if (_logger.IsInfo) _logger.Info($"Loading static nodes from file: {Path.GetFullPath(_staticNodesPath)}");
            var data = await File.ReadAllTextAsync(_staticNodesPath);
            var nodes = JsonConvert.DeserializeObject<string[]>(data).Distinct().ToArray();
            if (_logger.IsInfo) _logger.Info($"Loaded {nodes.Length} static nodes.");
            if (nodes.Length != 0)
            {
                if (_logger.IsInfo) _logger.Info($"Static nodes: {Environment.NewLine}{data}");
            }
            
            _nodes = new ConcurrentDictionary<PublicKey, NetworkNode>(nodes.Select(n => new NetworkNode(n))
                .ToDictionary(n => n.NodeId, n => n));
        }

        public async Task<bool> AddAsync(string enode, bool updateFile = true)
        {
            var node = new NetworkNode(enode);
            if (!_nodes.TryAdd(node.NodeId, node))
            {
                if (_logger.IsInfo) _logger.Info($"Static node was already added: {enode}");
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Static node was added: {enode}");
            NodeAdded?.Invoke(this, new NetworkNodeEventArgs(node));
            if (updateFile)
            {
                await SaveFileAsync();
            }

            return true;
        }

        public async Task<bool> RemoveAsync(string enode, bool updateFile = true)
        {
            var node = new NetworkNode(enode);
            if (!_nodes.TryRemove(node.NodeId, out _))
            {
                if (_logger.IsInfo) _logger.Info($"Static node already was not found: {enode}");
                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Static node was removed: {enode}");
            NodeRemoved?.Invoke(this, new NetworkNodeEventArgs(node));
            if (updateFile)
            {
                await SaveFileAsync();
            }

            return true;
        }

        private Task SaveFileAsync()
            => File.WriteAllTextAsync(_staticNodesPath,
                JsonConvert.SerializeObject(_nodes.Select(n => n.Value.ToString()), Formatting.Indented));
    }
}