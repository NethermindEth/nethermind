// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.State;

namespace Nethermind.State.Flat;

/// <summary>
/// Bounded store for warmed Patricia storage tries keyed by contract address.
/// </summary>
public sealed class PreservedStorageTries(int capacity)
{
    /// <summary>
    /// Repoints a preserved storage trie at the current scope resources.
    /// </summary>
    public delegate void Rebinder(SnapshotBundle bundle, ConcurrencyController quota);

    private readonly int _capacity = Math.Max(0, capacity);
    private readonly object _lock = new();
    private readonly Dictionary<AddressAsKey, Entry> _entries = new(Math.Max(0, capacity));
    private readonly Queue<AddressAsKey> _insertionOrder = new(Math.Max(0, capacity));

    /// <summary>
    /// Checks out a preserved storage trie when its anchor root matches the current account storage root.
    /// </summary>
    /// <returns><c>true</c> when a storage trie was reused; otherwise <c>false</c>.</returns>
    public bool TryTake(
        Address address,
        Hash256 storageRoot,
        SnapshotBundle newBundle,
        ConcurrencyController newQuota,
        out StorageTree tree,
        out Rebinder rebind)
    {
        lock (_lock)
        {
            AddressAsKey key = address;
            if (_entries.Remove(key, out Entry entry) && entry.StorageRoot == storageRoot)
            {
                entry.Rebind(newBundle, newQuota);
                tree = entry.Tree;
                rebind = entry.Rebind;
                return true;
            }

            tree = null!;
            rebind = null!;
            return false;
        }
    }

    /// <summary>
    /// Stores a committed storage trie for a later matching account storage root.
    /// </summary>
    public void Store(Address address, Hash256 storageRoot, StorageTree tree, Rebinder rebind)
    {
        if (_capacity <= 0 || storageRoot == Keccak.EmptyTreeHash || tree.RootRef is null) return;

        lock (_lock)
        {
            AddressAsKey key = address;
            _entries.Remove(key);
            _entries[key] = new Entry(storageRoot, tree, rebind);
            _insertionOrder.Enqueue(key);

            while (_entries.Count > _capacity)
            {
                EvictOldest();
            }
        }
    }

    private void EvictOldest()
    {
        while (_insertionOrder.TryDequeue(out AddressAsKey key))
        {
            if (_entries.Remove(key)) return;
        }
    }

    private readonly record struct Entry(Hash256 StorageRoot, StorageTree Tree, Rebinder Rebind);
}
