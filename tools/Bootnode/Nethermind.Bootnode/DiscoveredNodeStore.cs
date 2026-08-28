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
    internal const int DefaultNodePageSize = 1_000;
    internal const int MaxNodePageSize = 1_000;

    private readonly ConcurrentDictionary<Hash256, TrackedNode> _nodes = new();
    private readonly LinkedList<Hash256> _activeRetentionOrder = new();
    private readonly LinkedList<Hash256> _inactiveRetentionOrder = new();
    private readonly Dictionary<Hash256, RetentionEntry> _retentionEntries = [];
    private readonly SortedSet<Hash256> _orderedNodes = [];
    private readonly SortedSet<Hash256> _orderedActiveNodes = [];
    private readonly Lock _lock = new();
    private readonly int _maxRetainedNodes;
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
            TrackedNodeSnapshot after;
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? existing))
            {
                TrackedNodeSnapshot before = existing.CreateSnapshot();
                existing.Update(node, protocol, now, isActive);
                after = existing.CreateSnapshot();
                ApplyTransition(before, after);
                UpdateActiveIndex(node.IdHash, after.Active);
            }
            else
            {
                TrackedNode trackedNode = TrackedNode.Create(node, protocol, now, isActive);
                _nodes[node.IdHash] = trackedNode;
                _orderedNodes.Add(node.IdHash);
                after = trackedNode.CreateSnapshot();
                ApplyTransition(null, after);
                UpdateActiveIndex(node.IdHash, after.Active);
            }

            UpdateRetentionOrder(node.IdHash, after);
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

    public DiscoverySnapshot Remove(Node node, string protocol)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
            {
                TrackedNodeSnapshot before = trackedNode.CreateSnapshot();
                trackedNode.MarkInactive(protocol, DateTimeOffset.UtcNow);
                TrackedNodeSnapshot after = trackedNode.CreateSnapshot();
                ApplyTransition(before, after);
                UpdateActiveIndex(node.IdHash, after.Active);
                UpdateRetentionOrder(node.IdHash, after);
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

    public NodeDto[] GetActiveNodes(int offset = 0, int limit = DefaultNodePageSize) => GetNodes(activeOnly: true, offset, limit);

    public NodeDto[] GetAllNodes(int offset = 0, int limit = DefaultNodePageSize) => GetNodes(activeOnly: false, offset, limit);

    internal int RetentionOrderCount
    {
        get
        {
            lock (_lock)
            {
                return _retentionEntries.Count;
            }
        }
    }

    public DiscoverySnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return CreateSnapshotCore();
        }
    }

    internal static bool TryValidatePagination(int offset, int limit, out string error)
    {
        if (offset < 0)
        {
            error = "offset must be greater than or equal to 0.";
            return false;
        }

        if (limit is < 1 or > MaxNodePageSize)
        {
            error = $"limit must be between 1 and {MaxNodePageSize}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

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
        while (_allCount > _maxRetainedNodes)
        {
            LinkedListNode<Hash256>? oldestNode = _inactiveRetentionOrder.First ?? _activeRetentionOrder.First;
            if (oldestNode is null)
            {
                return;
            }

            RemoveRetentionEntry(oldestNode.Value);
            _orderedNodes.Remove(oldestNode.Value);
            _orderedActiveNodes.Remove(oldestNode.Value);
            if (_nodes.TryRemove(oldestNode.Value, out TrackedNode? trackedNode))
            {
                RemoveFromSnapshot(trackedNode.CreateSnapshot());
            }
        }
    }

    private void TouchRetentionOrder(Hash256 idHash, bool active)
    {
        LinkedListNode<Hash256> node;
        if (_retentionEntries.TryGetValue(idHash, out RetentionEntry existingEntry))
        {
            GetRetentionOrder(existingEntry.Active).Remove(existingEntry.Node);
            node = existingEntry.Node;
        }
        else
        {
            node = new LinkedListNode<Hash256>(idHash);
        }

        GetRetentionOrder(active).AddLast(node);
        _retentionEntries[idHash] = new RetentionEntry(node, active);
    }

    private void UpdateRetentionOrder(Hash256 idHash, TrackedNodeSnapshot snapshot)
    {
        if (snapshot.IsBootnode)
        {
            RemoveRetentionEntry(idHash);
            return;
        }

        TouchRetentionOrder(idHash, snapshot.Active);
    }

    private LinkedList<Hash256> GetRetentionOrder(bool active) =>
        active ? _activeRetentionOrder : _inactiveRetentionOrder;

    private void RemoveRetentionEntry(Hash256 idHash)
    {
        if (_retentionEntries.Remove(idHash, out RetentionEntry entry))
        {
            GetRetentionOrder(entry.Active).Remove(entry.Node);
        }
    }

    private NodeDto[] GetNodes(bool activeOnly, int offset, int limit)
    {
        if (!TryValidatePagination(offset, limit, out string error))
        {
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(limit), error);
        }

        List<TrackedNodeView> nodeViews;
        lock (_lock)
        {
            int nodeCount = activeOnly ? _activeCount : _allCount;
            nodeViews = new List<TrackedNodeView>(Math.Min(limit, nodeCount));
            int matchedNodes = 0;
            SortedSet<Hash256> orderedNodes = activeOnly ? _orderedActiveNodes : _orderedNodes;
            foreach (Hash256 idHash in orderedNodes)
            {
                if (!_nodes.TryGetValue(idHash, out TrackedNode? trackedNode))
                {
                    continue;
                }

                if (matchedNodes++ < offset)
                {
                    continue;
                }

                nodeViews.Add(trackedNode.CreateView());
                if (nodeViews.Count == limit)
                {
                    break;
                }
            }
        }

        NodeDto[] nodes = new NodeDto[nodeViews.Count];
        for (int i = 0; i < nodeViews.Count; i++)
        {
            nodes[i] = nodeViews[i].ToDto();
        }

        return nodes;
    }

    private void UpdateActiveIndex(Hash256 idHash, bool active)
    {
        if (active)
        {
            _orderedActiveNodes.Add(idHash);
        }
        else
        {
            _orderedActiveNodes.Remove(idHash);
        }
    }

    private sealed class TrackedNode
    {
        private readonly Lock _lock = new();
        private Node _node;
        private string _protocol;
        private bool _isBootnode;
        private string? _configuredEnode;
        private bool _activeDiscv4;
        private bool _activeDiscv5;
        private bool _activeConfigured;
        private DateTimeOffset _lastSeenUtc;
        private int _seenCount;

        private TrackedNode(Node node, string protocol, DateTimeOffset now, bool isActive)
        {
            _node = node;
            _protocol = protocol;
            _isBootnode = node.IsBootnode;
            _configuredEnode = node.IsBootnode ? node.ToString(Node.Format.ENode) : null;
            SetProtocolActive(protocol, isActive);
            FirstSeenUtc = now;
            _lastSeenUtc = now;
            _seenCount = 1;
        }

        private DateTimeOffset FirstSeenUtc { get; }

        public static TrackedNode Create(Node node, string protocol, DateTimeOffset now, bool isActive) =>
            new(node, protocol, now, isActive);

        public void Update(Node node, string protocol, DateTimeOffset now, bool isActive)
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
                if (isActive)
                {
                    SetProtocolActive(protocol, isActive: true);
                }
                _lastSeenUtc = now;
                _seenCount++;
            }
        }

        public void MarkInactive(string protocol, DateTimeOffset now)
        {
            lock (_lock)
            {
                SetProtocolActive(protocol, isActive: false);
                _lastSeenUtc = now;
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
                return new TrackedNodeSnapshot(_protocol, IsActiveCore, _isBootnode);
            }
        }

        public TrackedNodeView CreateView()
        {
            lock (_lock)
            {
                return new TrackedNodeView(
                    _node,
                    _protocol,
                    IsActiveCore,
                    FirstSeenUtc,
                    _lastSeenUtc,
                    _seenCount,
                    _isBootnode,
                    _configuredEnode);
            }
        }

        private bool IsActiveCore => _activeDiscv4 || _activeDiscv5 || _activeConfigured;

        private void SetProtocolActive(string protocol, bool isActive)
        {
            switch (protocol)
            {
                case "discv4":
                    _activeDiscv4 = isActive;
                    break;
                case "discv5":
                    _activeDiscv5 = isActive;
                    break;
                case "both":
                    _activeDiscv4 = isActive;
                    _activeDiscv5 = isActive;
                    break;
                case "configured":
                    _activeConfigured = isActive;
                    break;
                default:
                    throw new ArgumentException($"Unsupported discovery protocol '{protocol}'.", nameof(protocol));
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

    private readonly record struct TrackedNodeView(
        Node Node,
        string Protocol,
        bool Active,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        int SeenCount,
        bool IsBootnode,
        string? ConfiguredEnode)
    {
        public NodeDto ToDto() =>
            NodeDto.FromNode(Node, Protocol, Active, FirstSeenUtc, LastSeenUtc, SeenCount, IsBootnode, ConfiguredEnode);
    }

    private readonly record struct TrackedNodeSnapshot(string Protocol, bool Active, bool IsBootnode);

    private readonly record struct RetentionEntry(LinkedListNode<Hash256> Node, bool Active);
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
