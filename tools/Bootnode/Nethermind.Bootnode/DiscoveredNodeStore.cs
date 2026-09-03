// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class DiscoveredNodeStore
{
    private const int DefaultMaxRetainedNodes = 100_000;
    internal const int DefaultNodePageSize = 1_000;
    internal const int MaxNodePageSize = 1_000;

    private readonly Dictionary<Hash256, TrackedNode> _nodes;
    private readonly RetentionList _activeRetentionOrder = new(active: true);
    private readonly RetentionList _inactiveRetentionOrder = new(active: false);
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
        _nodes = new Dictionary<Hash256, TrackedNode>(maxRetainedNodes);
    }

    public void AddOrUpdate(Node node, string protocol, bool isActive)
    {
        NodeProtocol parsedProtocol = ParseProtocol(protocol);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            TrackedNodeSnapshot after;
            TrackedNode trackedNode;
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? existing))
            {
                trackedNode = existing;
                TrackedNodeSnapshot before = existing.CreateSnapshot();
                existing.Update(node, parsedProtocol, now, isActive);
                after = existing.CreateSnapshot();
                ApplyTransition(before, after);
                UpdateActiveIndex(node.IdHash, after.Active);
            }
            else
            {
                trackedNode = TrackedNode.Create(node, parsedProtocol, now, isActive);
                _nodes[node.IdHash] = trackedNode;
                _orderedNodes.Add(node.IdHash);
                after = trackedNode.CreateSnapshot();
                ApplyTransition(null, after);
                UpdateActiveIndex(node.IdHash, after.Active);
            }

            UpdateRetentionOrder(trackedNode, after);
            PruneRetainedNodes();
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

    public void Remove(Node node, string protocol)
    {
        NodeProtocol parsedProtocol = ParseProtocol(protocol);
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
            {
                TrackedNodeSnapshot before = trackedNode.CreateSnapshot();
                trackedNode.MarkInactive(parsedProtocol, DateTimeOffset.UtcNow);
                TrackedNodeSnapshot after = trackedNode.CreateSnapshot();
                ApplyTransition(before, after);
                UpdateActiveIndex(node.IdHash, after.Active);
                UpdateRetentionOrder(trackedNode, after);
                PruneRetainedNodes();
            }
        }
    }

    public string? GetProtocol(Node node)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(node.IdHash, out TrackedNode? trackedNode))
            {
                return trackedNode.GetProtocol();
            }

            return null;
        }
    }

    public NodeDto[] GetActiveNodes(int offset = 0, int limit = DefaultNodePageSize) => GetNodes(activeOnly: true, offset, limit);

    public NodeDto[] GetAllNodes(int offset = 0, int limit = DefaultNodePageSize) => GetNodes(activeOnly: false, offset, limit);

    internal int RetentionOrderCount
    {
        get
        {
            lock (_lock)
            {
                return _activeRetentionOrder.Count + _inactiveRetentionOrder.Count;
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
            case NodeProtocol.Discv4:
                _allDiscv4Count += delta;
                if (snapshot.Active) _activeDiscv4Count += delta;
                break;
            case NodeProtocol.Discv5:
                _allDiscv5Count += delta;
                if (snapshot.Active) _activeDiscv5Count += delta;
                break;
            case NodeProtocol.Both:
                _allBothCount += delta;
                if (snapshot.Active) _activeBothCount += delta;
                break;
            case NodeProtocol.Configured:
                _allConfiguredCount += delta;
                if (snapshot.Active) _activeConfiguredCount += delta;
                break;
        }
    }

    private void PruneRetainedNodes()
    {
        while (_allCount > _maxRetainedNodes)
        {
            TrackedNode? oldestNode = _inactiveRetentionOrder.First ?? _activeRetentionOrder.First;
            if (oldestNode is null)
            {
                return;
            }

            RemoveRetentionEntry(oldestNode);
            _orderedNodes.Remove(oldestNode.IdHash);
            _orderedActiveNodes.Remove(oldestNode.IdHash);
            if (_nodes.Remove(oldestNode.IdHash))
            {
                RemoveFromSnapshot(oldestNode.CreateSnapshot());
            }
        }
    }

    private void TouchRetentionOrder(TrackedNode trackedNode, bool active)
    {
        if (trackedNode.IsInRetentionOrder)
        {
            GetRetentionOrder(trackedNode.RetainedAsActive).Remove(trackedNode);
        }

        GetRetentionOrder(active).AddLast(trackedNode);
    }

    private void UpdateRetentionOrder(TrackedNode trackedNode, TrackedNodeSnapshot snapshot)
    {
        if (snapshot.IsBootnode)
        {
            RemoveRetentionEntry(trackedNode);
            return;
        }

        TouchRetentionOrder(trackedNode, snapshot.Active);
    }

    private RetentionList GetRetentionOrder(bool active) =>
        active ? _activeRetentionOrder : _inactiveRetentionOrder;

    private void RemoveRetentionEntry(TrackedNode trackedNode)
    {
        if (trackedNode.IsInRetentionOrder)
        {
            GetRetentionOrder(trackedNode.RetainedAsActive).Remove(trackedNode);
        }
    }

    private NodeDto[] GetNodes(bool activeOnly, int offset, int limit)
    {
        if (!TryValidatePagination(offset, limit, out string error))
        {
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(limit), error);
        }

        TrackedNodeView[] nodeViews;
        lock (_lock)
        {
            int nodeCount = activeOnly ? _activeCount : _allCount;
            int resultCount = Math.Min(limit, Math.Max(0, nodeCount - offset));
            if (resultCount == 0)
            {
                return [];
            }

            nodeViews = new TrackedNodeView[resultCount];
            int matchedNodes = 0;
            int resultIndex = 0;
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

                nodeViews[resultIndex++] = trackedNode.CreateView();
                if (resultIndex == resultCount)
                {
                    break;
                }
            }

            if (resultIndex != resultCount)
            {
                Array.Resize(ref nodeViews, resultIndex);
            }
        }

        NodeDto[] nodes = new NodeDto[nodeViews.Length];
        for (int i = 0; i < nodeViews.Length; i++)
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
        private readonly byte[] _nodeId;
        private IPAddress _host;
        private int _tcpPort;
        private int _discoveryPort;
        private NodeProtocol _protocols;
        private NodeProtocol _activeProtocols;
        private bool _isBootnode;
        private string? _configuredEnode;
        private byte[]? _enrRlp;
        private ulong _enrSequence;
        private long _lastSeenUtcTicks;
        private int _seenCount;

        private TrackedNode(Node node, NodeProtocol protocol, DateTimeOffset now, bool isActive)
        {
            IdHash = node.IdHash;
            _nodeId = node.Id.Bytes;
            _host = node.Address.Address;
            _tcpPort = node.Port;
            _discoveryPort = node.DiscoveryPort;
            _protocols = protocol;
            _isBootnode = node.IsBootnode;
            _configuredEnode = node.IsBootnode ? node.ToString(Node.Format.ENode) : null;
            UpdateEnr(node);
            SetProtocolActive(protocol, isActive);
            FirstSeenUtcTicks = now.UtcTicks;
            _lastSeenUtcTicks = now.UtcTicks;
            _seenCount = 1;
        }

        public Hash256 IdHash { get; }
        public bool IsActive => _activeProtocols != NodeProtocol.None;
        public bool IsInRetentionOrder { get; set; }
        public bool RetainedAsActive { get; set; }
        public TrackedNode? PreviousRetentionNode { get; set; }
        public TrackedNode? NextRetentionNode { get; set; }
        private long FirstSeenUtcTicks { get; }

        public static TrackedNode Create(Node node, NodeProtocol protocol, DateTimeOffset now, bool isActive) =>
            new(node, protocol, now, isActive);

        public void Update(Node node, NodeProtocol protocol, DateTimeOffset now, bool isActive)
        {
            if (node.IsBootnode)
            {
                _configuredEnode ??= node.ToString(Node.Format.ENode);
            }

            SetEndpoint(node);
            UpdateEnr(node);
            _isBootnode |= node.IsBootnode;
            _protocols |= protocol;
            if (isActive)
            {
                SetProtocolActive(protocol, isActive: true);
            }

            _lastSeenUtcTicks = now.UtcTicks;
            _seenCount++;
        }

        public void MarkInactive(NodeProtocol protocol, DateTimeOffset now)
        {
            SetProtocolActive(protocol, isActive: false);
            _lastSeenUtcTicks = now.UtcTicks;
        }

        public string GetProtocol() => ProtocolToString(GetProtocolCore());

        public TrackedNodeSnapshot CreateSnapshot() => new(GetProtocolCore(), IsActive, _isBootnode);

        public TrackedNodeView CreateView() =>
            new(
                _nodeId,
                IdHash,
                _host,
                _tcpPort,
                _discoveryPort,
                _configuredEnode,
                _enrRlp,
                GetProtocolCore(),
                IsActive,
                _isBootnode,
                FirstSeenUtcTicks,
                _lastSeenUtcTicks,
                _seenCount);

        private NodeProtocol GetProtocolCore()
        {
            NodeProtocol discoveryProtocols = _protocols & NodeProtocol.Both;
            return discoveryProtocols == NodeProtocol.None ? NodeProtocol.Configured : discoveryProtocols;
        }

        private void SetEndpoint(Node node)
        {
            _host = node.Address.Address;
            _tcpPort = node.Port;
            _discoveryPort = node.DiscoveryPort;
        }

        private void UpdateEnr(Node node)
        {
            NodeRecord? enr = node.Enr;
            if (enr?.Signature is null || (_enrRlp is not null && enr.EnrSequence <= _enrSequence))
            {
                return;
            }

            _enrRlp = enr.ToRlpBytes();
            _enrSequence = enr.EnrSequence;
        }

        private void SetProtocolActive(NodeProtocol protocol, bool isActive)
        {
            if (isActive)
            {
                _activeProtocols |= protocol;
            }
            else
            {
                _activeProtocols &= ~protocol;
            }
        }
    }

    private readonly record struct TrackedNodeView(
        byte[] NodeId,
        Hash256 IdHash,
        IPAddress Host,
        int TcpPort,
        int DiscoveryPort,
        string? Enode,
        byte[]? EnrRlp,
        NodeProtocol Protocol,
        bool Active,
        bool IsBootnode,
        long FirstSeenUtcTicks,
        long LastSeenUtcTicks,
        int SeenCount)
    {
        public NodeDto ToDto() =>
            new(
                NodeId.AsSpan().ToHexString(withZeroX: false),
                IdHash.ToString(),
                // Match Node.Host formatting without retaining the discovery Node graph.
                Host.IsIPv4MappedToIPv6 ? Host.MapToIPv4().ToString() : Host.ToString(),
                TcpPort,
                DiscoveryPort,
                Enode,
                EnrRlp is null ? null : string.Concat("enr:", Base64Url.EncodeToString(EnrRlp)),
                ProtocolToString(Protocol),
                Active,
                IsBootnode,
                new DateTimeOffset(FirstSeenUtcTicks, TimeSpan.Zero),
                new DateTimeOffset(LastSeenUtcTicks, TimeSpan.Zero),
                SeenCount);
    }

    private sealed class RetentionList(bool active)
    {
        public TrackedNode? First { get; private set; }
        public TrackedNode? Last { get; private set; }
        public int Count { get; private set; }

        public void AddLast(TrackedNode node)
        {
            Debug.Assert(!node.IsInRetentionOrder);
            node.PreviousRetentionNode = Last;
            node.NextRetentionNode = null;
            node.IsInRetentionOrder = true;
            node.RetainedAsActive = active;
            if (Last is null)
            {
                First = node;
            }
            else
            {
                Last.NextRetentionNode = node;
            }

            Last = node;
            Count++;
        }

        /// <remarks>The caller must ensure <paramref name="node"/> belongs to this list.</remarks>
        public void Remove(TrackedNode node)
        {
            Debug.Assert(node.IsInRetentionOrder && node.RetainedAsActive == active);
            if (node.PreviousRetentionNode is null)
            {
                First = node.NextRetentionNode;
            }
            else
            {
                node.PreviousRetentionNode.NextRetentionNode = node.NextRetentionNode;
            }

            if (node.NextRetentionNode is null)
            {
                Last = node.PreviousRetentionNode;
            }
            else
            {
                node.NextRetentionNode.PreviousRetentionNode = node.PreviousRetentionNode;
            }

            node.PreviousRetentionNode = null;
            node.NextRetentionNode = null;
            node.IsInRetentionOrder = false;
            Count--;
        }
    }

    private static NodeProtocol ParseProtocol(string protocol) => protocol switch
    {
        "discv4" => NodeProtocol.Discv4,
        "discv5" => NodeProtocol.Discv5,
        "both" => NodeProtocol.Both,
        "configured" => NodeProtocol.Configured,
        _ => throw new ArgumentException($"Unsupported discovery protocol '{protocol}'.", nameof(protocol))
    };

    private static string ProtocolToString(NodeProtocol protocol) => protocol switch
    {
        NodeProtocol.Discv4 => "discv4",
        NodeProtocol.Discv5 => "discv5",
        NodeProtocol.Both => "both",
        NodeProtocol.Configured => "configured",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };

    [Flags]
    private enum NodeProtocol : byte
    {
        None = 0,
        Discv4 = 1,
        Discv5 = 2,
        Both = Discv4 | Discv5,
        Configured = 4
    }

    private readonly record struct TrackedNodeSnapshot(NodeProtocol Protocol, bool Active, bool IsBootnode);
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
