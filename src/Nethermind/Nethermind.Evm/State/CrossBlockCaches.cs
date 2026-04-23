// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block caches that survive block boundaries on the canonical chain.
/// Only storage-slot values are kept here; account objects are intentionally excluded.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<StorageCell, byte[], HugeCacheSets> _storageCache = new();
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<StorageCell, byte[], HugeCacheSets> StorageCache => _storageCache;

    public long LastCommittedBlockNumber
    {
        get => Volatile.Read(ref _lastCommittedBlockNumber);
        set => Volatile.Write(ref _lastCommittedBlockNumber, value);
    }
}
