// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Flat;

/// <summary>
/// Retains warmed storage Patricia tries across consecutive writable flat-state scopes.
/// </summary>
public sealed class PreservedStorageTries
{
    public delegate void Rebinder(SnapshotBundle bundle, ConcurrencyController quota);

    private const int MaxStoredTries = 2048;

    private readonly ConcurrentDictionary<AddressAsKey, Entry> _entries = [];
    private readonly ConcurrentQueue<(AddressAsKey Key, int Generation)> _insertionOrder = new();
    private int _generation;

    public bool TryTake(
        Address address,
        Hash256 storageRoot,
        SnapshotBundle newBundle,
        ConcurrencyController newQuota,
        out StorageTree tree,
        out Rebinder rebinder)
    {
        if (storageRoot == Keccak.EmptyTreeHash)
        {
            tree = null!;
            rebinder = null!;
            return false;
        }

        AddressAsKey key = address;
        if (!_entries.TryRemove(key, out Entry? entry))
        {
            tree = null!;
            rebinder = null!;
            return false;
        }

        if (entry.StorageRoot != storageRoot)
        {
            tree = null!;
            rebinder = null!;
            return false;
        }

        entry.Rebind(newBundle, newQuota);
        tree = entry.Tree;
        rebinder = entry.Rebind;
        return true;
    }

    public void Store(Address address, StorageTree tree, Rebinder rebinder, Hash256 storageRoot)
    {
        if (storageRoot == Keccak.EmptyTreeHash) return;

        AddressAsKey key = address;
        int generation = unchecked(Interlocked.Increment(ref _generation));
        _entries[key] = new Entry(tree, rebinder, storageRoot, generation);
        _insertionOrder.Enqueue((key, generation));
        Trim();
    }

    private void Trim()
    {
        while (_entries.Count > MaxStoredTries && _insertionOrder.TryDequeue(out (AddressAsKey Key, int Generation) item))
        {
            if (_entries.TryGetValue(item.Key, out Entry? entry) && entry.Generation == item.Generation)
            {
                KeyValuePair<AddressAsKey, Entry> staleEntry = new(item.Key, entry);
                ((ICollection<KeyValuePair<AddressAsKey, Entry>>)_entries).Remove(staleEntry);
            }
        }
    }

    private sealed record Entry(StorageTree Tree, Rebinder Rebind, Hash256 StorageRoot, int Generation);
}
