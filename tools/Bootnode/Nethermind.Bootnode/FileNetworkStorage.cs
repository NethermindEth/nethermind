// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.Bootnode;

internal sealed class FileNetworkStorage(string path, ILogManager logManager) : INetworkStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Lock _lock = new();
    private readonly string _path = path;
    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<FileNetworkStorage>();
    private Dictionary<string, PersistedNetworkNode>? _nodes;
    private Dictionary<string, PersistedNetworkNode>? _batch;

    public int PersistedNodesCount
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _nodes!.Count;
            }
        }
    }

    public NetworkNode[] GetPersistedNodes()
    {
        lock (_lock)
        {
            EnsureLoaded();
            NetworkNode[] nodes = new NetworkNode[_nodes!.Count];
            int index = 0;
            foreach (PersistedNetworkNode persistedNode in _nodes.Values)
            {
                try
                {
                    nodes[index++] = new NetworkNode(persistedNode.Node) { Reputation = persistedNode.Reputation };
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping invalid persisted discovery node '{persistedNode.Node}': {exception.Message}");
                }
            }

            if (index == nodes.Length)
            {
                return nodes;
            }

            NetworkNode[] validNodes = new NetworkNode[index];
            Array.Copy(nodes, validNodes, index);
            return validNodes;
        }
    }

    public void UpdateNode(NetworkNode node)
    {
        lock (_lock)
        {
            Dictionary<string, PersistedNetworkNode> target = GetWriteTarget();
            target[GetKey(node.NodeId)] = PersistedNetworkNode.FromNetworkNode(node);
            SaveIfUnbatched();
        }
    }

    public void UpdateNodes(IEnumerable<NetworkNode> nodes)
    {
        lock (_lock)
        {
            Dictionary<string, PersistedNetworkNode> target = GetWriteTarget();
            foreach (NetworkNode node in nodes)
            {
                target[GetKey(node.NodeId)] = PersistedNetworkNode.FromNetworkNode(node);
            }

            SaveIfUnbatched();
        }
    }

    public void RemoveNode(PublicKey nodeId)
    {
        lock (_lock)
        {
            Dictionary<string, PersistedNetworkNode> target = GetWriteTarget();
            target.Remove(GetKey(nodeId));
            SaveIfUnbatched();
        }
    }

    public void StartBatch()
    {
        lock (_lock)
        {
            EnsureLoaded();
            _batch = [];
        }
    }

    public void Commit()
    {
        lock (_lock)
        {
            if (_batch is null)
            {
                return;
            }

            _nodes = _batch;
            _batch = null;
            Save();
        }
    }

    public bool AnyPendingChange()
    {
        lock (_lock)
        {
            return _batch is not null;
        }
    }

    private Dictionary<string, PersistedNetworkNode> GetWriteTarget()
    {
        EnsureLoaded();
        return _batch ?? _nodes!;
    }

    private void SaveIfUnbatched()
    {
        if (_batch is null)
        {
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_nodes is not null)
        {
            return;
        }

        if (!File.Exists(_path))
        {
            _nodes = [];
            return;
        }

        try
        {
            PersistedNetworkNode[] persistedNodes = JsonSerializer.Deserialize<PersistedNetworkNode[]>(File.ReadAllText(_path), JsonOptions) ?? [];
            Dictionary<string, PersistedNetworkNode> nodes = new(persistedNodes.Length);
            for (int i = 0; i < persistedNodes.Length; i++)
            {
                NetworkNode networkNode = new(persistedNodes[i].Node);
                nodes[GetKey(networkNode.NodeId)] = persistedNodes[i];
            }

            _nodes = nodes;
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to load discovery storage '{_path}': {exception.Message}");
            _nodes = [];
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PersistedNetworkNode[] persistedNodes = new PersistedNetworkNode[_nodes!.Count];
        int index = 0;
        foreach (PersistedNetworkNode node in _nodes.Values)
        {
            persistedNodes[index++] = node;
        }

        string tempPath = $"{_path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(persistedNodes, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    private static string GetKey(PublicKey publicKey) => publicKey.ToString(false);

    private readonly record struct PersistedNetworkNode(string Node, long Reputation)
    {
        public static PersistedNetworkNode FromNetworkNode(NetworkNode node) => new(node.ToString(), node.Reputation);
    }
}
