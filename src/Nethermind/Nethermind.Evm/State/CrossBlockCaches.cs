// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block caches that survive block boundaries on the canonical chain.
/// Invalidated on commit failure or block discontinuity.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<StorageCell, byte[], LargeCacheSets> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account, LargeCacheSets> _accountCache = new();
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<StorageCell, byte[], LargeCacheSets> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account, LargeCacheSets> AccountCache => _accountCache;

    public long LastCommittedBlockNumber
    {
        get => Volatile.Read(ref _lastCommittedBlockNumber);
        set => Volatile.Write(ref _lastCommittedBlockNumber, value);
    }

    public void Clear()
    {
        _storageCache.Clear();
        _accountCache.Clear();
    }
}
