// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block caches for storage slots and account state. These survive across blocks
/// and are updated via write-through during block commit. Unlike <see cref="PreBlockCaches"/>,
/// these are never shared with the prewarmer — only the main processing thread reads/writes them.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;

    /// <summary>
    /// Block number of the last successfully committed block. Used to detect reorgs:
    /// if the next scope's base block doesn't match this, the caches are stale and must be cleared.
    /// </summary>
    public long LastCommittedBlockNumber
    {
        get => Volatile.Read(ref _lastCommittedBlockNumber);
        set => Volatile.Write(ref _lastCommittedBlockNumber, value);
    }
}
