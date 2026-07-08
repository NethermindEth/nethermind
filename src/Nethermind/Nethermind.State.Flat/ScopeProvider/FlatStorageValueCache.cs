// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Caches flat storage values for writable flat scopes across consecutive block-processing scopes.
/// </summary>
public sealed class FlatStorageValueCache
{
    private const int StorageCacheSetsBits = 17;

    private readonly SeqlockCache<StorageCell, byte[]> _cache = new(StorageCacheSetsBits);
    private readonly object _stateLock = new();
    private StateId? _stateId;

    internal FlatStorageValueCache()
    {
    }

    internal void ResetIfStateChanged(StateId stateId)
    {
        lock (_stateLock)
        {
            if (_stateId == stateId) return;

            _cache.Clear();
            _stateId = stateId;
        }
    }

    internal void AcceptState(StateId stateId)
    {
        lock (_stateLock)
        {
            _stateId = stateId;
        }
    }

    internal void Clear()
    {
        lock (_stateLock)
        {
            _cache.Clear();
            _stateId = null;
        }
    }

    internal bool TryGet(Address address, in UInt256 index, out byte[] value)
    {
        StorageCell storageCell = new(address, in index);
        if (_cache.TryGetValue(in storageCell, out byte[]? cached))
        {
            value = cached ?? StorageTree.ZeroBytes;
            return true;
        }

        value = null!;
        return false;
    }

    internal byte[] Set(Address address, in UInt256 index, byte[]? value)
    {
        byte[] cached = value is null || value.Length == 0 ? StorageTree.ZeroBytes : value;
        StorageCell storageCell = new(address, in index);
        _cache.Set(in storageCell, cached);
        return cached;
    }
}
