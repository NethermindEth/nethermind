// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public interface ILocations<in TKey>
{
    int GetLocation(TKey address);
}

public class Locations : ILocations<Address>, ILocations<StorageCell>
{
    private int _addressIndex = -1;
    private int _storageIndex = 0;
    private readonly ConcurrentDictionary<AddressAsKey, int> _addressLocations = new();
    private readonly ConcurrentDictionary<StorageCell, int> _storageCellLocations = new();

    public int GetLocation(Address address) => _addressLocations.GetOrAdd(address, Interlocked.Increment(ref _addressIndex));
    public int GetLocation(StorageCell storageCell) => _storageCellLocations.GetOrAdd(storageCell, Interlocked.Decrement(ref _storageIndex));

    public void Clear()
    {
        _addressIndex = -1;
        _addressLocations.NoResizeClear();
        _storageCellLocations.NoResizeClear();
    }
}
