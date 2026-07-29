// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class DiscoveredNodeStore
{
    private readonly ConcurrentDictionary<Hash256, TrackedNode> _nodes = new();

    public DiscoverySnapshot AddOrUpdate(Node node, string protocol, bool isActive)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _nodes.AddOrUpdate(
            node.IdHash,
            _ => TrackedNode.Create(node, protocol, now, isActive),
            (_, existing) =>
            {
                existing.Update(node, protocol, now, isActive);
                return existing;
            });

        return CreateSnapshot();
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
        if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
        {
            trackedNode.MarkInactive(DateTimeOffset.UtcNow);
        }

        return CreateSnapshot();
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
        int activeCount = 0;
        int allCount = 0;
        int activeDiscv4Count = 0;
        int allDiscv4Count = 0;
        int activeDiscv5Count = 0;
        int allDiscv5Count = 0;
        int activeBothCount = 0;
        int allBothCount = 0;
        int activeConfiguredCount = 0;
        int allConfiguredCount = 0;

        foreach (TrackedNode trackedNode in _nodes.Values)
        {
            TrackedNodeSnapshot trackedNodeSnapshot = trackedNode.CreateSnapshot();
            allCount++;
            if (trackedNodeSnapshot.Active)
            {
                activeCount++;
            }

            IncrementProtocolCounts(
                trackedNodeSnapshot.Protocol,
                trackedNodeSnapshot.Active,
                ref activeDiscv4Count,
                ref allDiscv4Count,
                ref activeDiscv5Count,
                ref allDiscv5Count,
                ref activeBothCount,
                ref allBothCount,
                ref activeConfiguredCount,
                ref allConfiguredCount);
        }

        return new DiscoverySnapshot(
            activeCount,
            allCount,
            activeDiscv4Count,
            allDiscv4Count,
            activeDiscv5Count,
            allDiscv5Count,
            activeBothCount,
            allBothCount,
            activeConfiguredCount,
            allConfiguredCount);
    }

    public static string InferProtocol(Node node) => string.IsNullOrEmpty(node.Enr) ? "discv4" : "discv5";

    private static void IncrementProtocolCounts(
        string protocol,
        bool active,
        ref int activeDiscv4Count,
        ref int allDiscv4Count,
        ref int activeDiscv5Count,
        ref int allDiscv5Count,
        ref int activeBothCount,
        ref int allBothCount,
        ref int activeConfiguredCount,
        ref int allConfiguredCount)
    {
        switch (protocol)
        {
            case "discv4":
                allDiscv4Count++;
                if (active) activeDiscv4Count++;
                break;
            case "discv5":
                allDiscv5Count++;
                if (active) activeDiscv5Count++;
                break;
            case "both":
                allBothCount++;
                if (active) activeBothCount++;
                break;
            case "configured":
                allConfiguredCount++;
                if (active) activeConfiguredCount++;
                break;
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
                _active = _active || isActive;
                _lastSeenUtc = now;
                _seenCount++;
            }
        }

        public void MarkInactive(DateTimeOffset now)
        {
            lock (_lock)
            {
                _active = false;
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
                return new TrackedNodeSnapshot(_protocol, _active);
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

    private readonly record struct TrackedNodeSnapshot(string Protocol, bool Active);
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
