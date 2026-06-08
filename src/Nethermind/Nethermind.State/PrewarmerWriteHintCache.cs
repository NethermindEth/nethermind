// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State;

public sealed class PrewarmerWriteHintCache
{
    private static readonly object Marker = new();
    private readonly SeqlockCache<StorageCell, object> _storageWrites = new();
    private int _hasStorageWrites;

    public bool HasStorageWrites => Volatile.Read(ref _hasStorageWrites) != 0;

    public void AddStorageWrite(Address address, in UInt256 index)
    {
        StorageCell storageCell = new(address, in index);
        _storageWrites.Set(in storageCell, Marker);
        Volatile.Write(ref _hasStorageWrites, 1);
    }

    public bool MightWrite(Address address, in UInt256 index)
    {
        StorageCell storageCell = new(address, in index);
        return _storageWrites.TryGetValue(in storageCell, out _);
    }

    public void Clear()
    {
        Volatile.Write(ref _hasStorageWrites, 0);
        _storageWrites.Clear();
    }
}
