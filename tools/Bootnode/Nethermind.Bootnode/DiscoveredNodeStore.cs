// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class DiscoveredNodeStore
{
    private const int DefaultMaxRetainedNodes = 100_000;

    private readonly ConcurrentDictionary<Hash256, TrackedNode> _nodes = new();
    private readonly Queue<RetainedNodeKey> _retentionOrder = new();
    private readonly Lock _lock = new();
    private readonly int _maxRetainedNodes;
    private long _version;
    private int _activeCount;
    private int _allCount;
    private int _activeDiscv4Count;
    private int _allDiscv4Count;
    private int _activeDiscv5Count;
    private int _allDiscv5Count;
    private int _activeBothCount;
    private int _allBothCount;
    private int _activeConfiguredCount;
    private int _allConfiguredCount;

    public DiscoveredNodeStore(int maxRetainedNodes = DefaultMaxRetainedNodes)
    {
        BootnodeOptionValidation.ValidatePositive(nameof(maxRetainedNodes), maxRetainedNodes);
        _maxRetainedNodes = maxRetainedNodes;
    }

    public DiscoverySnapshot AddOrUpdate(Node node, string protocol, bool isActive)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            long version = ++_version;
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? existing))
            {
                TrackedNodeSnapshot before = existing.CreateSnapshot();
                existing.Update(node, protocol, now, isActive, version);
                ApplyTransition(before, existing.CreateSnapshot());
            }
            else
            {
                TrackedNode trackedNode = TrackedNode.Create(node, protocol, now, isActive, version);
                _nodes[node.IdHash] = trackedNode;
                ApplyTransition(null, trackedNode.CreateSnapshot());
            }

            _retentionOrder.Enqueue(new RetainedNodeKey(node.IdHash, version));
            PruneRetainedNodes();
            return CreateSnapshotCore();
        }
    }

    public DiscoverySnapshot AddConfiguredBootnodes(NetworkNode[] bootnodes)
    {
        for (int i = 0; i < bootnodes.Length; i++)
        {
            Node node = new(bootnodes[i])
            {
                IsBootnode = true
            };
            AddOrUpdate(node, "configured", isActive: false);
        }

        return CreateSnapshot();
    }

    public DiscoverySnapshot Remove(Node node)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
            {
                long version = ++_version;
                TrackedNodeSnapshot before = trackedNode.CreateSnapshot();
                trackedNode.MarkInactive(DateTimeOffset.UtcNow, version);
                ApplyTransition(before, trackedNode.CreateSnapshot());
                _retentionOrder.Enqueue(new RetainedNodeKey(node.IdHash, version));
                PruneRetainedNodes();
            }

            return CreateSnapshotCore();
        }
    }

    public string? GetProtocol(Node node)
    {
        if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
        {
            return trackedNode.GetProtocol();
        }

        return null;
    }

    public NodeDto[] GetActiveNodes() => GetNodes(activeOnly: true);

    public NodeDto[] GetAllNodes() => GetNodes(activeOnly: false);

    public DiscoverySnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return CreateSnapshotCore();
        }
    }

    public static string InferProtocol(Node node) => node.Enr is null ? "discv4" : "discv5";

    private DiscoverySnapshot CreateSnapshotCore() =>
        new(
            _activeCount,
            _allCount,
            _activeDiscv4Count,
            _allDiscv4Count,
            _activeDiscv5Count,
            _allDiscv5Count,
            _activeBothCount,
            _allBothCount,
            _activeConfiguredCount,
            _allConfiguredCount);

    private void ApplyTransition(TrackedNodeSnapshot? before, TrackedNodeSnapshot after)
    {
        if (before.HasValue)
        {
            ApplyCounts(before.GetValueOrDefault(), -1);
        }

        ApplyCounts(after, 1);
    }

    private void RemoveFromSnapshot(TrackedNodeSnapshot snapshot) => ApplyCounts(snapshot, -1);

    private void ApplyCounts(TrackedNodeSnapshot snapshot, int delta)
    {
        _allCount += delta;
        if (snapshot.Active)
        {
            _activeCount += delta;
        }

        switch (snapshot.Protocol)
        {
            case "discv4":
                _allDiscv4Count += delta;
                if (snapshot.Active) _activeDiscv4Count += delta;
                break;
            case "discv5":
                _allDiscv5Count += delta;
                if (snapshot.Active) _activeDiscv5Count += delta;
                break;
            case "both":
                _allBothCount += delta;
                if (snapshot.Active) _activeBothCount += delta;
                break;
            case "configured":
                _allConfiguredCount += delta;
                if (snapshot.Active) _activeConfiguredCount += delta;
                break;
        }
    }

    private void PruneRetainedNodes()
    {
        while (_nodes.Count > _maxRetainedNodes && _retentionOrder.Count != 0)
        {
            RetainedNodeKey retainedNodeKey = _retentionOrder.Dequeue();
            if (!_nodes.TryGetValue(retainedNodeKey.IdHash, out TrackedNode? trackedNode))
            {
                continue;
            }

            TrackedNodeSnapshot snapshot = trackedNode.CreateSnapshot();
            if (snapshot.Version != retainedNodeKey.Version)
            {
                continue;
            }

            if (_nodes.TryRemove(retainedNodeKey.IdHash, out _))
            {
                RemoveFromSnapshot(snapshot);
            }
        }
    }

    private NodeDto[] GetNodes(bool activeOnly)
    {
        List<NodeDto> nodes = [];
        foreach (TrackedNode trackedNode in _nodes.Values)
        {
            NodeDto? dto = trackedNode.ToDto(activeOnly);
            if (dto is not null) nodes.Add(dto);
        }

        nodes.Sort(static (left, right) => string.CompareOrdinal(left.NodeId, right.NodeId));
        return [.. nodes];
    }

    private sealed class TrackedNode
    {
        private readonly Lock _lock = new();
        private Node _node;
        private string _protocol;
        private bool _isBootnode;
        private string? _configuredEnode;
        private bool _active;
        private DateTimeOffset _lastSeenUtc;
        private int _seenCount;

        private TrackedNode(Node node, string protocol, DateTimeOffset now, bool isActive)
        {
            _node = node;
            _protocol = protocol;
            _isBootnode = node.IsBootnode;
            _configuredEnode = node.IsBootnode ? node.ToString(Node.Format.ENode) : null;
            _active = isActive;
            FirstSeenUtc = now;
            _lastSeenUtc = now;
            _seenCount = 1;
        }

        private DateTimeOffset FirstSeenUtc { get; }

        private long _version;

        public static TrackedNode Create(Node node, string protocol, DateTimeOffset now, bool isActive, long version) =>
            new(node, protocol, now, isActive)
            {
                _version = version
            };

        public void Update(Node node, string protocol, DateTimeOffset now, bool isActive, long version)
        {
            lock (_lock)
            {
                if (node.IsBootnode)
                {
                    _configuredEnode ??= node.ToString(Node.Format.ENode);
                }

                _node = node;
                _isBootnode |= node.IsBootnode;
                _protocol = MergeProtocol(_protocol, protocol);
                _active = _active || isActive;
                _lastSeenUtc = now;
                _seenCount++;
                _version = version;
            }
        }

        public void MarkInactive(DateTimeOffset now, long version)
        {
            lock (_lock)
            {
                _active = false;
                _lastSeenUtc = now;
                _version = version;
            }
        }

        public string GetProtocol()
        {
            lock (_lock)
            {
                return _protocol;
            }
        }

        public TrackedNodeSnapshot CreateSnapshot()
        {
            lock (_lock)
            {
                return new TrackedNodeSnapshot(_protocol, _active, _version);
            }
        }

        public NodeDto? ToDto(bool activeOnly)
        {
            lock (_lock)
            {
                if (activeOnly && !_active)
                {
                    return null;
                }

                return NodeDto.FromNode(_node, _protocol, _active, FirstSeenUtc, _lastSeenUtc, _seenCount, _isBootnode, _configuredEnode);
            }
        }

        private static string MergeProtocol(string current, string next)
        {
            if (current == next)
            {
                return current;
            }

            if (current == "configured")
            {
                return next;
            }

            if (next == "configured")
            {
                return current;
            }

            return "both";
        }
    }

    private readonly record struct RetainedNodeKey(Hash256 IdHash, long Version);

    private readonly record struct TrackedNodeSnapshot(string Protocol, bool Active, long Version);
}

internal readonly record struct DiscoverySnapshot(
    int ActiveCount,
    int AllCount,
    int ActiveDiscv4Count,
    int AllDiscv4Count,
    int ActiveDiscv5Count,
    int AllDiscv5Count,
    int ActiveBothCount,
    int AllBothCount,
    int ActiveConfiguredCount,
    int AllConfiguredCount);
