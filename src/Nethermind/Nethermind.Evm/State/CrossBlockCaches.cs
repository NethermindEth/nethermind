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
    // setsLog2=20 → 2M entries. StorageCell key ~52 bytes + byte[32] value ≈ 170 MB.
    // Storage is the hottest cross-block cache — SLOAD-heavy blocks reuse the same contract slots.
    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new(setsLog2: 20);

    // setsLog2=18 → 512K entries. AddressAsKey ~20 bytes + Account ~100 bytes ≈ 60 MB.
    // Fewer unique accounts than storage slots per block, so smaller is fine.
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new(setsLog2: 18);
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
