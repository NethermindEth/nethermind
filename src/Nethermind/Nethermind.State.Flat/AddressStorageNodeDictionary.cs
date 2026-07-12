// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using Nethermind.Core;
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
    private readonly ConcurrentDictionary<Hash256AsKey, AddressNodes> _byAddress = new();

    public int Count
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<Hash256AsKey, AddressNodes> address in _byAddress) count += address.Value.Nodes.Count;
            return count;
        }
    }

    public TrieNode this[HashedKey<(Hash256, TreePath)> key]
    {
        set => GetOrAddAddress(key.Key.Item1).Set(key.Key.Item2, value);
    }

    internal AddressNodes GetOrAddAddress(Hash256 address) =>
        _byAddress.GetOrAdd(address, static _ => new AddressNodes());

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

    public Enumerator GetEnumerator() => new(_byAddress.GetEnumerator());

    IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>> IEnumerable<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal sealed class AddressNodes
    {
        internal Dictionary<HashedKey<TreePath>, TrieNode> Nodes { get; } = [];

        internal void EnsureAdditionalCapacity(int additionalCapacity) =>
            Nodes.EnsureCapacity(Nodes.Count + additionalCapacity);

        internal void Set(in TreePath path, TrieNode node) => Nodes[path] = node;
    }

    public struct Enumerator : IEnumerator<KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode>>
    {
        private IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> _addresses;
        private Dictionary<HashedKey<TreePath>, TrieNode>.Enumerator _nodes;
        private Hash256 _address;
        private bool _hasNodes;

        internal Enumerator(IEnumerator<KeyValuePair<Hash256AsKey, AddressNodes>> addresses)
        {
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

            while (_addresses.MoveNext())
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
            _nodes.Dispose();
            _addresses.Dispose();
        }
    }
}
