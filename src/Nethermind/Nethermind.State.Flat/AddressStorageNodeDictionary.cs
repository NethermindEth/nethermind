// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Stores changed storage-trie nodes in address-owned dictionaries.
/// </summary>
/// <remarks>
/// A storage-trie commit exclusively owns one address, so sequential commits write directly to its inner dictionary.
/// Parallel subtree commits buffer their nodes and publish them after joining.
/// </remarks>
public sealed class AddressStorageNodeDictionary : IReadOnlyCollection<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
{
    private const int MaxPooledNodeDictionaries = 1_024;
    private const int MaxPooledNodeCapacity = 4_096;

    private readonly ConcurrentDictionary<Hash256AsKey, AddressNodes> _byAddress = new();
    private IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>>? _cachedAddressEnumerator;

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

        AddressNodes candidate = AddressNodesPool.Rent();
        nodes = _byAddress.GetOrAdd(address, candidate);
        if (!ReferenceEquals(nodes, candidate)) AddressNodesPool.Return(candidate);
        return nodes;
    }

    public bool TryGetValue(HashedKey<(Hash256, TreePath)> key, out TrieNode node)
    {
        if (_byAddress.TryGetValue(key.Key.Item1, out AddressNodes? nodes))
        {
            return nodes.Nodes.TryGetValue(key.Key.Item2, out node!);
        }

        node = null!;
        return false;
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
        internal Dictionary<HashedKey<TreePath>, TrieNode> Nodes { get; } = [];

        internal void EnsureAdditionalCapacity(int additionalCapacity) =>
            Nodes.EnsureCapacity(Nodes.Count + additionalCapacity);

        internal void Set(in TreePath path, TrieNode node) => Nodes[path] = node;
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
            if (nodes.Nodes.Capacity > MaxPooledNodeCapacity) return;

            if (Interlocked.Increment(ref _count) > MaxPooledNodeDictionaries)
            {
                Interlocked.Decrement(ref _count);
                return;
            }

            nodes.Nodes.Clear();
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
