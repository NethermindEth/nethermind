// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block caches that survive block boundaries on the canonical chain.
/// Values are kept conservative and must be invalidated on discontinuity or aborted execution.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<AddressAsKey, Account> _accountCache = new();
    private readonly SeqlockCache<StorageCell, byte[], LargeCacheSets> _storageCache = new();
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<AddressAsKey, Account> AccountCache => _accountCache;
    public SeqlockCache<StorageCell, byte[], LargeCacheSets> StorageCache => _storageCache;

    public long LastCommittedBlockNumber
    {
        get => Volatile.Read(ref _lastCommittedBlockNumber);
        set => Volatile.Write(ref _lastCommittedBlockNumber, value);
    }
}
