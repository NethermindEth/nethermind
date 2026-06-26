// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Torrent;

internal readonly struct KadId : IEquatable<KadId>
{
    public const int Length = 20;
    private readonly byte[] _bytes;

    public KadId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException("Kademlia identifiers are 20 bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static KadId Random()
    {
        byte[] bytes = new byte[Length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return new KadId(bytes);
    }

    public bool Equals(KadId other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is KadId other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        for (int i = 0; i < _bytes.Length; i += 4)
        {
            hashCode.Add(BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(i, 4)));
        }

        return hashCode.ToHashCode();
    }

    public override string ToString() => Convert.ToHexString(_bytes).ToLowerInvariant();
}

internal readonly record struct DhtNode(KadId Id, IPEndPoint EndPoint);

internal sealed class TorrentKademlia
{
    private readonly DhtKeyOperator _keyOperator = new();
    private readonly INodeHashProvider<DhtNode> _nodeHashProvider;
    private readonly IRoutingTable<DhtNode> _routingTable;
    private readonly ILookupAlgo<DhtNode> _lookup;
    private readonly INodeHealthTracker<DhtNode> _nodeHealthTracker;
    private readonly KadId _selfId;

    public TorrentKademlia(KadId selfId, int k = 16, int alpha = 3)
    {
        _selfId = selfId;
        DhtNode selfNode = new(selfId, new IPEndPoint(IPAddress.Any, 0));
        KademliaConfig<DhtNode> config = new()
        {
            CurrentNodeId = selfNode,
            KSize = k,
            Alpha = alpha,
            LookupFindNeighbourHardTimeout = TimeSpan.FromSeconds(5),
            NodeRequestFailureThreshold = 2,
        };
        _nodeHashProvider = new FromKeyNodeHashProvider<KadId, DhtNode>(_keyOperator);
        _routingTable = new KBucketTree<DhtNode>(config, _nodeHashProvider, LimboLogs.Instance);
        _nodeHealthTracker = new TorrentNodeHealthTracker(_routingTable, _nodeHashProvider);
        _lookup = new LookupKNearestNeighbour<KadId, DhtNode>(
            _routingTable,
            _nodeHashProvider,
            _nodeHealthTracker,
            config,
            LimboLogs.Instance);
    }

    public void AddOrRefresh(DhtNode node)
    {
        if (node.Id.Equals(_selfId))
        {
            return;
        }

        _nodeHealthTracker.OnIncomingMessageFrom(node);
    }

    public void Remove(DhtNode node) => _routingTable.Remove(_nodeHashProvider.GetHash(node));

    public List<DhtNode> GetClosest(KadId target, int count)
    {
        ValueHash256 targetHash = DhtKeyOperator.ToValueHash(target);
        DhtNode[] nodes = _routingTable.GetKNearestNeighbour(DhtKeyOperator.ToValueHash(target));
        Array.Sort(nodes, (left, right) =>
            Hash256XorUtils.Compare(
                _nodeHashProvider.GetHash(left),
                _nodeHashProvider.GetHash(right),
                targetHash));
        if (nodes.Length <= count)
        {
            return [.. nodes];
        }

        List<DhtNode> closest = new(count);
        for (int i = 0; i < count; i++)
        {
            closest.Add(nodes[i]);
        }

        return closest;
    }

    public async Task<List<DhtNode>> LookupAsync(
        KadId target,
        Func<DhtNode, CancellationToken, Task<IReadOnlyList<DhtNode>?>> query,
        CancellationToken token,
        int maxFreshCandidates = 256)
    {
        object candidateLock = new();
        HashSet<ValueHash256> knownHashes = GetKnownHashes();
        HashSet<ValueHash256> returnedCandidateHashes = [DhtKeyOperator.ToValueHash(_selfId)];
        int remainingFreshCandidates = maxFreshCandidates;
        DhtNode[] nodes = await _lookup.Lookup(
            DhtKeyOperator.ToValueHash(target),
            16,
            async (node, lookupToken) =>
            {
                IReadOnlyList<DhtNode>? neighbours = await query(node, lookupToken);
                if (neighbours is null)
                {
                    return null;
                }

                List<DhtNode> result = [];
                lock (candidateLock)
                {
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        ValueHash256 neighbourHash = _nodeHashProvider.GetHash(neighbours[i]);
                        if (!returnedCandidateHashes.Add(neighbourHash))
                        {
                            continue;
                        }

                        if (knownHashes.Contains(neighbourHash))
                        {
                            result.Add(neighbours[i]);
                            continue;
                        }

                        if (remainingFreshCandidates > 0)
                        {
                            result.Add(neighbours[i]);
                            remainingFreshCandidates--;
                        }
                    }
                }

                return result.ToArray();
            },
            token);

        return [.. nodes];
    }

    private HashSet<ValueHash256> GetKnownHashes()
    {
        HashSet<ValueHash256> hashes = [DhtKeyOperator.ToValueHash(_selfId)];
        foreach ((ValueHash256 _, int _, KBucket<DhtNode> bucket) in _routingTable.IterateBuckets())
        {
            (ValueHash256, DhtNode)[] items = bucket.GetAllWithHash();
            for (int i = 0; i < items.Length; i++)
            {
                hashes.Add(items[i].Item1);
            }
        }

        return hashes;
    }
}

internal sealed class DhtKeyOperator : IKeyOperator<KadId, DhtNode>
{
    public KadId GetKey(DhtNode node) => node.Id;

    public ValueHash256 GetKeyHash(KadId key) => ToValueHash(key);

    public KadId CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        ValueHash256 hash = Hash256XorUtils.GetRandomHashAtDistance(nodePrefix, depth);
        return new KadId(hash.BytesAsSpan[..KadId.Length]);
    }

    public static ValueHash256 ToValueHash(KadId id)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        bytes.Clear();
        id.Bytes.CopyTo(bytes[..KadId.Length]);
        return new ValueHash256(bytes);
    }

    public static int CompareDistance(KadId left, KadId right, KadId target)
        => Hash256XorUtils.Compare(ToValueHash(left), ToValueHash(right), ToValueHash(target));
}

internal sealed class TorrentNodeHealthTracker(
    IRoutingTable<DhtNode> routingTable,
    INodeHashProvider<DhtNode> nodeHashProvider) : INodeHealthTracker<DhtNode>
{
    public void OnIncomingMessageFrom(DhtNode sender)
        => routingTable.TryAddOrRefresh(nodeHashProvider.GetHash(sender), sender, out _);

    public void OnRequestFailed(DhtNode node) => routingTable.Remove(nodeHashProvider.GetHash(node));
}
