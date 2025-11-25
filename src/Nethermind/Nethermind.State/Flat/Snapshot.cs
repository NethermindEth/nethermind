// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Snapshot are written keys between state From to state To
/// </summary>
/// <param name="From"></param>
/// <param name="To"></param>
/// <param name="Accounts"></param>
/// <param name="Storages"></param>
public class Snapshot(
    StateId from,
    StateId to,
    Dictionary<AddressPrefixAsKey, Account?> accounts,
    Dictionary<(AddressPrefixAsKey, UInt256), byte[]?> storages,
    HashSet<AddressPrefixAsKey> selfDestructedStorageAddresses,
    Dictionary<(Hash256PrefixAsKey, TreePath), TrieNode> trieNodes)
{
    public StateId From => from;
    public StateId To => to;
    public IEnumerable<KeyValuePair<AddressPrefixAsKey, Account?>> Accounts => accounts;
    public IEnumerable<AddressPrefixAsKey> SelfDestructedStorageAddresses => selfDestructedStorageAddresses;
    public IEnumerable<KeyValuePair<(AddressPrefixAsKey, UInt256), byte[]?>> Storages => storages;
    public IEnumerable<KeyValuePair<(Hash256PrefixAsKey, TreePath), TrieNode>> TrieNodes => trieNodes;
    public int AccountsCount => accounts.Count;
    public int StoragesCount => storages.Count;
    public int TrieNodesCount => trieNodes.Count;

    public bool TryGetAccount(AddressPrefixAsKey key, out Account acc)
    {
        return accounts.TryGetValue(key, out acc);
    }

    public bool HasSelfDestruct(Address address)
    {
        return selfDestructedStorageAddresses.Contains(address);
    }

    public bool TryGetStorage(Address address, in UInt256 index, out byte[] value)
    {
        return storages.TryGetValue((address, index), out value);
    }

    public bool TryGetTrieNodes(Hash256 address, in TreePath path, out TrieNode node)
    {
        return trieNodes.TryGetValue((address, path), out node);
    }
}
