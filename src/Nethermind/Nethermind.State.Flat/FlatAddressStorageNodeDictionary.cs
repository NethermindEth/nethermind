// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Slab-backed counterpart of <see cref="AddressStorageNodeDictionary"/>: stores changed storage-trie
/// nodes as packed <see cref="SlabHandle"/>s in address-owned handle dictionaries over one shared
/// <see cref="SlabArena"/>. Reads decode transient <see cref="TrieNode"/>s from the arena bytes.
/// </summary>
/// <remarks>
/// A storage-trie commit exclusively owns one address, so its handle dictionary has at most one writer,
/// exactly as the object-backed variant. The append target is a single arena shared by all addresses —
/// per-address arenas would round every address up to a whole 1MiB slab — so appends fan out over a
/// small fixed set of lock-free cursors (each grabs slabs exclusively) that pack thousands of addresses
/// into a handful of partial slabs. Reads must not overlap writes to the same address; enumeration and
/// <see cref="Count"/> require all addresses to be quiescent.
/// </remarks>
public sealed class FlatAddressStorageNodeDictionary : IReadOnlyCollection<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
{
    private const int ShardCountLog2 = 4;
    private const int ShardCount = 1 << ShardCountLog2;
    private const int MaxPooledNodeDictionaries = 1_024;
    private const int MaxPooledNodeCapacity = 4_096;

    private readonly ConcurrentDictionary<Hash256AsKey, FlatAddressNodes> _byAddress = new();
    private readonly SlabArena _arena = new();
    private readonly long[] _shardCursors = new long[ShardCount];
    private IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>>? _cachedAddressEnumerator;

    public FlatAddressStorageNodeDictionary() => ResetCursors();

    private void ResetCursors()
    {
        for (int i = 0; i < ShardCount; i++) _shardCursors[i] = SlabArena.EmptySharedCursor;
    }

    private static int ShardOf(Hash256 address) => (int)((uint)address.GetHashCode() >> (32 - ShardCountLog2));

    internal SlabArena Arena => _arena;
    public long ArenaBytesReserved => _arena.BytesReserved;

    public int Count
    {
        get
        {
            int count = 0;
            IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> addresses = RentAddressEnumerator();
            try
            {
                while (addresses.MoveNext()) count += addresses.Current.Value.Count;
            }
            finally
            {
                ReturnAddressEnumerator(addresses);
            }
            return count;
        }
    }

    public TrieNode this[HashedKey<(Hash256, TreePath)> key]
    {
        set => GetOrAddAddress(key.Key.Item1).Set(key.Key.Item2, value);
    }

    internal FlatAddressNodes GetOrAddAddress(Hash256 address)
    {
        if (_byAddress.TryGetValue(address, out FlatAddressNodes? nodes)) return nodes;

        FlatAddressNodes candidate = FlatAddressNodesPool.Rent();
        candidate.Init(_arena, _shardCursors, ShardOf(address));
        nodes = _byAddress.GetOrAdd(address, candidate);
        if (!ReferenceEquals(nodes, candidate)) FlatAddressNodesPool.Return(candidate);
        return nodes;
    }

    public bool TryGetValue(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
    {
        if (_byAddress.TryGetValue(key.Key.Item1, out FlatAddressNodes? nodes))
        {
            return nodes.TryGet(key.Key.Item2, out node);
        }

        node = null;
        return false;
    }

    internal bool RemoveAddress(Hash256 address) => _byAddress.TryRemove(address, out _);

    public void Clear()
    {
        _byAddress.Clear();
        ResetCursors();
        _arena.Release();
    }

    internal void NoLockClear()
    {
        IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> addresses = RentAddressEnumerator();
        try
        {
            while (addresses.MoveNext()) FlatAddressNodesPool.Return(addresses.Current.Value);
        }
        finally
        {
            ReturnAddressEnumerator(addresses);
        }

        _byAddress.NoLockClear();
        ResetCursors();
        _arena.Release();
    }

    /// <summary>Releases the arena's slabs to the shared pool. Only valid at the content's quiescent
    /// disposal boundary.</summary>
    internal void ReleaseArena() => _arena.Release();

    /// <summary>Raw-span visit of every stored record; caller must hold a lease for the full enumeration.</summary>
    public void ForEachRlp(RlpVisitor<HashedKey<(Hash256, TreePath)>> visitor)
    {
        IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> addresses = RentAddressEnumerator();
        try
        {
            while (addresses.MoveNext())
            {
                Hash256 address = addresses.Current.Key;
                foreach (KeyValuePair<HashedKey<TreePath>, ulong> handle in addresses.Current.Value.Handles)
                {
                    SlabHandle slabHandle = new(handle.Value);
                    _arena.ReadSpan(slabHandle, out _, out ReadOnlySpan<byte> rlp);
                    HashedKey<(Hash256, TreePath)> key = new((address, handle.Key.Key));
                    visitor(in key, (slabHandle.Flags & SlabFlags.EmptyUnknown) != 0, rlp);
                }
            }
        }
        finally
        {
            ReturnAddressEnumerator(addresses);
        }
    }

    public Enumerator GetEnumerator() => new(this, RentAddressEnumerator(), _arena, _arena.Generation);

    IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> RentAddressEnumerator() =>
        Interlocked.Exchange(ref _cachedAddressEnumerator, null) ?? _byAddress.GetEnumerator();

    private void ReturnAddressEnumerator(IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> enumerator)
    {
        enumerator.Reset();
        if (Interlocked.CompareExchange(ref _cachedAddressEnumerator, enumerator, null) is not null)
        {
            enumerator.Dispose();
        }
    }

    internal static TrieNode Materialize(Hash256? keccak, byte[]? rlpCopy)
    {
        if (rlpCopy is null || rlpCopy.Length == 0)
        {
            return new TrieNode(NodeType.Unknown, keccak!);
        }

        if (SlabArena.DebugChecks && keccak is not null && ValueKeccak.Compute(rlpCopy) != keccak)
        {
            throw new InvalidOperationException("Slab record keccak mismatch — slab lifetime bug");
        }

        return keccak is not null
            ? new TrieNode(NodeType.Unknown, keccak, new CappedArray<byte>(rlpCopy))
            : new TrieNode(NodeType.Unknown, new CappedArray<byte>(rlpCopy));
    }

    internal sealed class FlatAddressNodes
    {
        internal Dictionary<HashedKey<TreePath>, ulong> Handles { get; } = [];
        private SlabArena _arena = null!;
        private long[] _cursors = null!;
        private int _shard;

        internal void Init(SlabArena arena, long[] cursors, int shard)
        {
            _arena = arena;
            _cursors = cursors;
            _shard = shard;
        }

        internal int Count => Handles.Count;

        internal void EnsureAdditionalCapacity(int additionalCapacity) =>
            Handles.EnsureCapacity(Handles.Count + additionalCapacity);

        internal void Set(in TreePath path, TrieNode node)
        {
            SlabFlags flags = SlabFlags.None;
            CappedArray<byte> fullRlp = node.FullRlp;
            ReadOnlySpan<byte> rlp = fullRlp.IsNotNull ? fullRlp.AsSpan() : default;
            if (rlp.Length == 0)
            {
                if (node.Keccak is null) throw new InvalidOperationException("Cannot publish a node with neither RLP nor keccak to flat node storage");
                if (node.NodeType == NodeType.Unknown) flags |= SlabFlags.EmptyUnknown;
            }

            SlabHandle handle = _arena.AppendShared(ref _cursors[_shard], node.Keccak, rlp, flags);
            Handles[path] = handle.Packed;
        }

        internal bool TryGet(in TreePath path, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            int generation = _arena.Generation;
            if (!Handles.TryGetValue(path, out ulong packed)) return false;

            SlabHandle handle = new(packed);
            if (handle.IsNone || !_arena.TryReadCopy(handle, generation, out Hash256? keccak, out byte[]? rlp)) return false;
            node = Materialize(keccak, rlp);
            return true;
        }

        internal void ClearForPool()
        {
            Handles.Clear();
            _arena = null!;
            _cursors = null!;
        }
    }

    private static class FlatAddressNodesPool
    {
        private static readonly ConcurrentQueue<FlatAddressNodes> Pool = [];
        private static int _count;

        public static FlatAddressNodes Rent()
        {
            if (Volatile.Read(ref _count) > 0 && Pool.TryDequeue(out FlatAddressNodes? nodes))
            {
                Interlocked.Decrement(ref _count);
                return nodes;
            }

            return new FlatAddressNodes();
        }

        public static void Return(FlatAddressNodes nodes)
        {
            if (nodes.Handles.Capacity > MaxPooledNodeCapacity) return;

            if (Interlocked.Increment(ref _count) > MaxPooledNodeDictionaries)
            {
                Interlocked.Decrement(ref _count);
                return;
            }

            nodes.ClearForPool();
            Pool.Enqueue(nodes);
        }
    }

    public struct Enumerator : IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
    {
        private FlatAddressStorageNodeDictionary? _owner;
        private IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>>? _addresses;
        private Dictionary<HashedKey<TreePath>, ulong>.Enumerator _handles;
        private readonly SlabArena _arena;
        private readonly int _generation;
        private Hash256 _address;
        private HashedKey<(Hash256, TreePath)> _currentKey;
        private TrieNode _currentNode;
        private bool _hasNodes;

        internal Enumerator(
            FlatAddressStorageNodeDictionary owner,
            IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>> addresses,
            SlabArena arena,
            int generation)
        {
            _owner = owner;
            _addresses = addresses;
            _arena = arena;
            _generation = generation;
            _handles = default;
            _address = null!;
            _currentKey = default;
            _currentNode = null!;
            _hasNodes = false;
        }

        public readonly KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> Current => new(_currentKey, _currentNode);

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (true)
            {
                if (_hasNodes && _handles.MoveNext())
                {
                    KeyValuePair<HashedKey<TreePath>, ulong> entry = _handles.Current;
                    SlabHandle handle = new(entry.Value);
                    if (handle.IsNone || !_arena.TryReadCopy(handle, _generation, out Hash256? keccak, out byte[]? rlp))
                    {
                        // A handle present in the map but unreadable means the arena was released or its
                        // generation rolled mid-enumeration. Every consumer of this enumerator (compaction
                        // merge, EnumerateStorageNodes) runs under an arena lease, so this cannot happen by
                        // design; silently skipping would drop a live storage node from the merged snapshot,
                        // so fail loud rather than emit a torn result.
                        throw new InvalidOperationException(
                            $"Flat storage node {entry.Key.Key} for {_address} unreadable from the slab arena during a lease-covered enumeration (handle={entry.Value}, generation={_generation}); the arena lease is not held.");
                    }

                    _currentKey = new HashedKey<(Hash256, TreePath)>((_address, entry.Key.Key));
                    _currentNode = Materialize(keccak, rlp);
                    return true;
                }

                if (!_addresses!.MoveNext()) return false;
                KeyValuePair<Hash256AsKey, FlatAddressNodes> address = _addresses.Current;
                _address = address.Key;
                _handles = address.Value.Handles.GetEnumerator();
                _hasNodes = true;
            }
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            IEnumerator<KeyValuePair<Hash256AsKey, FlatAddressNodes>>? addresses = _addresses;
            if (addresses is null) return;

            _addresses = null;
            _handles.Dispose();
            FlatAddressStorageNodeDictionary owner = _owner!;
            _owner = null;
            owner.ReturnAddressEnumerator(addresses);
        }
    }
}
