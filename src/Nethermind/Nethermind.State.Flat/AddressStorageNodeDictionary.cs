// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Collections;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Stores changed storage-trie nodes in address-owned dictionaries.
/// </summary>
/// <remarks>
/// A storage-trie commit exclusively owns one address, so sequential commits write directly to its inner dictionary.
/// Parallel subtree commits buffer their nodes and publish them after joining.
/// The per-address dictionaries are not thread-safe: each address must have at most one writer, and reads must not
/// overlap writes to that address. Enumeration and <see cref="Count"/> require all addresses to be quiescent.
/// </remarks>
public sealed class AddressStorageNodeDictionary : IReadOnlyCollection<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
{
    private const int MaxPooledNodeDictionaries = 1_024;
    private const int PooledNodeCapacity = 4_096;
    // A contract touching tens of thousands of slots in one block needs a dictionary far above the pooled
    // capacity; trimming it on return made every such block re-grow it through LOH-sized doublings. Those
    // dictionaries are retained whole for the process lifetime instead, under a total entry budget (a measured
    // ~132 B per entry including its bucket, so at most ~280 MB); nothing hands the memory back, since the
    // contracts that need it recur.
    private const long MaxRetainedLargeNodeEntries = 2L * 1024 * 1024;

    private readonly ConcurrentDictionary<Hash256AsKey, AddressNodes> _byAddress = new();
    private IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>>? _cachedAddressEnumerator;
    // Set before any insert and cleared only by NoLockClear, so it is never false while addresses remain.
    private bool _mayHaveAddresses;

    internal bool MayHaveAddresses => _mayHaveAddresses;

    public int Count
    {
        get
        {
            int count = 0;
            IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> addresses = RentAddressEnumerator();
            try
            {
                while (addresses.MoveNext()) count += addresses.Current.Value.Nodes.Count;
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

    internal AddressNodes GetOrAddAddress(Hash256 address)
    {
        if (_byAddress.TryGetValue(address, out AddressNodes? nodes)) return nodes;

        _mayHaveAddresses = true;
        AddressNodes candidate = AddressNodesPool.Rent();
        nodes = _byAddress.GetOrAdd(address, candidate);
        if (!ReferenceEquals(nodes, candidate)) AddressNodesPool.Return(candidate);
        return nodes;
    }

    public bool TryGetValue(HashedKey<(Hash256, TreePath)> key, out TrieNode node) => TryGetValue(key.Key.Item1, key.Key.Item2, out node);

    /// <summary>Looks a node up without the combined key, whose hash this dictionary does not use.</summary>
    public bool TryGetValue(Hash256 address, in TreePath path, out TrieNode node)
    {
        if (_byAddress.TryGetValue(address, out AddressNodes? nodes))
        {
            return nodes.Nodes.TryGetValue(path, out node!);
        }

        node = null!;
        return false;
    }

    /// <summary>Adds the <see cref="HashedKey{T}"/> hash code of every key to <paramref name="filter"/>.</summary>
    /// <remarks>
    /// Combines each address hash, computed once, with the cached path hashes as the key tuple does, so no combined
    /// key is built and no path is rehashed.
    /// </remarks>
    internal void AddKeyHashesTo(SplitBlockFilter filter)
    {
        IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> addresses = RentAddressEnumerator();
        try
        {
            while (addresses.MoveNext())
            {
                int addressHash = addresses.Current.Key.GetHashCode();
                foreach (HashedKey<TreePath> path in addresses.Current.Value.Nodes.Keys)
                {
                    filter.Add((uint)HashCode.Combine(addressHash, path.GetHashCode()));
                }
            }
        }
        finally
        {
            ReturnAddressEnumerator(addresses);
        }
    }

    internal bool RemoveAddress(Hash256 address) => _byAddress.TryRemove(address, out _);

    public void Clear() => _byAddress.Clear();

    internal void NoLockClear()
    {
        IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> addresses = RentAddressEnumerator();
        try
        {
            while (addresses.MoveNext()) AddressNodesPool.Return(addresses.Current.Value);
        }
        finally
        {
            ReturnAddressEnumerator(addresses);
        }

        _byAddress.NoLockClear();
        _mayHaveAddresses = false;
    }

    public Enumerator GetEnumerator() => new(this, RentAddressEnumerator());

    IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> RentAddressEnumerator() =>
        Interlocked.Exchange(ref _cachedAddressEnumerator, null) ?? _byAddress.GetEnumerator();

    private void ReturnAddressEnumerator(IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> enumerator)
    {
        enumerator.Reset();
        if (Interlocked.CompareExchange(ref _cachedAddressEnumerator, enumerator, null) is not null)
        {
            enumerator.Dispose();
        }
    }

    internal sealed class AddressNodes
    {
        private Dictionary<HashedKey<TreePath>, TrieNode>? _spare;

        internal Dictionary<HashedKey<TreePath>, TrieNode> Nodes { get; private set; } = [];

        internal void EnsureAdditionalCapacity(int additionalCapacity)
        {
            int required = Nodes.Count + additionalCapacity;
            if (required <= Nodes.Capacity) return;

            // An empty dictionary about to take a large batch swaps in a retained large one instead of growing.
            if (Nodes.Count == 0 && required > PooledNodeCapacity
                && LargeNodeDictionaryPool.TryRent(required, out Dictionary<HashedKey<TreePath>, TrieNode>? large))
            {
                _spare = Nodes;
                Nodes = large;
                return;
            }

            Nodes.EnsureCapacity(required);
        }

        internal void Set(in TreePath path, TrieNode node) => Nodes[path] = node;

        internal void ResetForPooling()
        {
            if (Nodes.Capacity > PooledNodeCapacity)
            {
                Dictionary<HashedKey<TreePath>, TrieNode> large = Nodes;
                // Keep a small spare when one exists; otherwise use an empty fallback while the large batch returns to the shared pool.
                Nodes = _spare ?? [];
                _spare = null;
                large.Clear();
                LargeNodeDictionaryPool.Return(large);
            }

            Nodes.ClearAndTrim(PooledNodeCapacity, PooledNodeCapacity);
        }
    }

    private static class LargeNodeDictionaryPool
    {
        private static readonly Lock Lock = new();
        private static readonly List<Dictionary<HashedKey<TreePath>, TrieNode>> Retained = [];
        private static long _retainedEntries;

        /// <summary>Takes the smallest retained dictionary with at least <paramref name="minCapacity"/> entries of capacity.</summary>
        public static bool TryRent(int minCapacity, [NotNullWhen(true)] out Dictionary<HashedKey<TreePath>, TrieNode>? dictionary)
        {
            lock (Lock)
            {
                int best = -1;
                for (int i = 0; i < Retained.Count; i++)
                {
                    int capacity = Retained[i].Capacity;
                    if (capacity >= minCapacity && (best < 0 || capacity < Retained[best].Capacity)) best = i;
                }

                if (best < 0)
                {
                    dictionary = null;
                    return false;
                }

                dictionary = Retained[best];
                Retained.RemoveAt(best);
                _retainedEntries -= dictionary.Capacity;
                return true;
            }
        }

        /// <summary>Retains a cleared dictionary while the total retained capacity stays within budget; otherwise it is dropped.</summary>
        public static void Return(Dictionary<HashedKey<TreePath>, TrieNode> dictionary)
        {
            lock (Lock)
            {
                if (_retainedEntries + dictionary.Capacity > MaxRetainedLargeNodeEntries) return;

                Retained.Add(dictionary);
                _retainedEntries += dictionary.Capacity;
            }
        }
    }

    private static class AddressNodesPool
    {
        private static readonly ConcurrentQueue<AddressNodes> Pool = [];
        private static int _count;

        public static AddressNodes Rent()
        {
            if (Volatile.Read(ref _count) > 0 && Pool.TryDequeue(out AddressNodes? nodes))
            {
                Interlocked.Decrement(ref _count);
                return nodes;
            }

            return new AddressNodes();
        }

        public static void Return(AddressNodes nodes)
        {
            if (Interlocked.Increment(ref _count) > MaxPooledNodeDictionaries)
            {
                Interlocked.Decrement(ref _count);
                return;
            }

            nodes.ResetForPooling();
            Pool.Enqueue(nodes);
        }
    }

    public struct Enumerator : IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
    {
        private AddressStorageNodeDictionary? _owner;
        private IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>>? _addresses;
        private Dictionary<HashedKey<TreePath>, TrieNode>.Enumerator _nodes;
        private Hash256 _address;
        private bool _hasNodes;

        internal Enumerator(
            AddressStorageNodeDictionary owner,
            IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> addresses)
        {
            _owner = owner;
            _addresses = addresses;
            _nodes = default;
            _address = null!;
            _hasNodes = false;
        }

        public readonly KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> Current
        {
            get
            {
                KeyValuePair<HashedKey<TreePath>, TrieNode> node = _nodes.Current;
                return new((_address, node.Key.Key), node.Value);
            }
        }

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_hasNodes && _nodes.MoveNext()) return true;

            while (_addresses!.MoveNext())
            {
                KeyValuePair<Hash256AsKey, AddressNodes> address = _addresses.Current;
                _address = address.Key;
                _nodes = address.Value.Nodes.GetEnumerator();
                _hasNodes = true;
                if (_nodes.MoveNext()) return true;
            }

            return false;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>>? addresses = _addresses;
            if (addresses is null) return;

            _addresses = null;
            _nodes.Dispose();
            AddressStorageNodeDictionary owner = _owner!;
            _owner = null;
            owner.ReturnAddressEnumerator(addresses);
        }
    }
}
