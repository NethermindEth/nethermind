// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using Nethermind.Kademlia;

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
    private readonly INodeHashProvider<DhtNode, KadId> _nodeHashProvider;
    private readonly IRoutingTable<DhtNode, KadId> _routingTable;
    private readonly ILookupAlgo<DhtNode, KadId> _lookup;
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
        _nodeHashProvider = new FromKeyNodeHashProvider<KadId, DhtNode, KadId>(_keyOperator);
        _routingTable = new KBucketTree<DhtNode, KadId>(config, _nodeHashProvider, KadDistance.Instance);
        _nodeHealthTracker = new TorrentNodeHealthTracker(_routingTable, _nodeHashProvider);
        _lookup = new LookupKNearestNeighbour<KadId, DhtNode, KadId>(
            _routingTable,
            _nodeHashProvider,
            KadDistance.Instance,
            _nodeHealthTracker,
            config);
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
        DhtNode[] nodes = _routingTable.GetKNearestNeighbour(target, excludeSelf: true);
        Array.Sort(nodes, (left, right) =>
            KadDistance.Instance.Compare(
                _nodeHashProvider.GetHash(left),
                _nodeHashProvider.GetHash(right),
                target));
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
        HashSet<KadId> knownHashes = GetKnownHashes();
        HashSet<KadId> returnedCandidateHashes = [_selfId];
        int remainingFreshCandidates = maxFreshCandidates;
        DhtNode[] nodes = await _lookup.Lookup(
            target,
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
                        KadId neighbourHash = _nodeHashProvider.GetHash(neighbours[i]);
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

    private HashSet<KadId> GetKnownHashes()
    {
        HashSet<KadId> hashes = [_selfId];
        foreach (RoutingTableBucket<DhtNode, KadId> bucket in _routingTable.IterateBuckets())
        {
            for (int i = 0; i < bucket.Nodes.Count; i++)
            {
                hashes.Add(_nodeHashProvider.GetHash(bucket.Nodes[i]));
            }
        }

        return hashes;
    }
}

internal sealed class DhtKeyOperator : IKeyOperator<KadId, DhtNode, KadId>
{
    public KadId GetKey(DhtNode node) => node.Id;

    public KadId GetKeyHash(KadId key) => key;

    public KadId CreateRandomKeyAtDistance(KadId nodePrefix, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, KadId.Length * 8);

        Span<byte> bytes = stackalloc byte[KadId.Length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        int bitIndex = KadId.Length * 8 - depth;
        int byteIndex = bitIndex / 8;
        nodePrefix.Bytes[..byteIndex].CopyTo(bytes);
        int bitWithinByte = 7 - bitIndex % 8;
        byte prefixMask = (byte)(0xFF << (bitWithinByte + 1));
        byte distanceBit = (byte)(1 << bitWithinByte);
        bytes[byteIndex] = (byte)((bytes[byteIndex] & ~(prefixMask | distanceBit)) |
            (nodePrefix.Bytes[byteIndex] & prefixMask) |
            ((~nodePrefix.Bytes[byteIndex]) & distanceBit));
        return new KadId(bytes);
    }

    public static int CompareDistance(KadId left, KadId right, KadId target)
        => KadDistance.Instance.Compare(left, right, target);
}

internal sealed class KadDistance : IKademliaDistance<KadId>
{
    public static KadDistance Instance { get; } = new();

    public int MaxDistance => KadId.Length * 8;

    public KadId Zero { get; } = new(new byte[KadId.Length]);

    public int CalculateLogDistance(KadId left, KadId right)
    {
        for (int i = 0; i < KadId.Length; i++)
        {
            byte xor = (byte)(left.Bytes[i] ^ right.Bytes[i]);
            if (xor != 0)
            {
                return MaxDistance - i * 8 - (BitOperations.LeadingZeroCount((uint)xor) - 24);
            }
        }

        return 0;
    }

    public int Compare(KadId left, KadId right, KadId target)
    {
        for (int i = 0; i < KadId.Length; i++)
        {
            int comparison = (left.Bytes[i] ^ target.Bytes[i]).CompareTo(right.Bytes[i] ^ target.Bytes[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public bool GetBit(KadId key, int index)
        => (key.Bytes[index / 8] & (1 << (7 - index % 8))) != 0;

    public KadId SetBit(KadId key, int index)
    {
        byte[] bytes = key.Bytes.ToArray();
        bytes[index / 8] |= (byte)(1 << (7 - index % 8));
        return new KadId(bytes);
    }
}

internal sealed class TorrentNodeHealthTracker(
    IRoutingTable<DhtNode, KadId> routingTable,
    INodeHashProvider<DhtNode, KadId> nodeHashProvider) : INodeHealthTracker<DhtNode>
{
    public void OnIncomingMessageFrom(DhtNode sender)
        => routingTable.TryAddOrRefresh(nodeHashProvider.GetHash(sender), sender, out _);

    public void OnRequestFailed(DhtNode node) => routingTable.Remove(nodeHashProvider.GetHash(node));
}
