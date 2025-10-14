// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.FlatCache;

#pragma warning disable CS9113 // Parameter is unread.
public class StorageSnapshotBundle(ArrayPoolList<StorageWrites> storages, IBigCache.IStorageReader bigCacheStorage, Address address) : IDisposable
#pragma warning restore CS9113 // Parameter is unread.
{
    Dictionary<UInt256, byte[]> _changedSlots = new();
    ArrayPoolList<(Address, UInt256)> _writtenSlots = new(0);
    internal bool _hasSelfDestruct = false;

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }
        if (_changedSlots.TryGetValue(index, out value))
        {
            return true;
        }

        for (int i = storages.Count - 1; i >= 0; i--)
        {
            if (storages[i].Slots.TryGetValue(index, out value))
            {
                return true;
            }

            if (storages[i].HasSelfDestruct)
            {
                return true;
            }
        }

        return bigCacheStorage.TryGetValue(index, out value);
    }

    public void ApplyStateChanges(Dictionary<UInt256, byte[]> changedValues, bool hasSelfDestruct)
    {
        if (hasSelfDestruct)
        {
            _changedSlots.Clear();
            _hasSelfDestruct = true;
        }
        foreach (var kv in changedValues)
        {
            _writtenSlots.Add((address, kv.Key));
            _changedSlots[kv.Key] = kv.Value;
        }
    }

    public void HintGet(in UInt256 index, byte[]? value)
    {
        _changedSlots[index] = value;
    }

    public (StorageWrites, ArrayPoolList<(Address, UInt256)>) CollectAndApplyKnownState()
    {
        StorageWrites storageWrites = new StorageWrites()
        {
            HasSelfDestruct = _hasSelfDestruct,
            Slots = _changedSlots,
        };

        _changedSlots = new();
        _hasSelfDestruct = false;
        storages.Add(storageWrites);

        // TODO: Could be empty
        return (storageWrites, _writtenSlots);
    }

    public void Dispose()
    {
        storages.Dispose();
    }
}
