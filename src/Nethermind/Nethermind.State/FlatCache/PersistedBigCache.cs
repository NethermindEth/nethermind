// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.FlatCache;

public class PersistedBigCache: IBigCache
{
    public long CurrentBlockNumber { get; }
    public int SnapshotCount { get; }
    public bool TryGetValue(Address address, out Account acc)
    {
        throw new System.NotImplementedException();
    }

    public IBigCache.IStorageReader GetStorageReader(Address address)
    {
        throw new System.NotImplementedException();
    }

    public void Subtract(StateId firstKey, Snapshot snapshot)
    {
        throw new System.NotImplementedException();
    }

    public void Add(StateId pickedSnapshot, Snapshot pickedState)
    {
        throw new System.NotImplementedException();
    }
}
