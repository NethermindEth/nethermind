// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Caches storage-slot and account values that survive across blocks, sitting one tier below the
/// per-block <see cref="PreBlockCaches"/>. Seeded from trie reads and kept current via write-through,
/// it turns the cold first-touch reads that dominate SLOAD (and account reads behind CALL) into cache
/// hits on subsequent blocks, avoiding the underlying RocksDB point read.
/// </summary>
/// <remarks>
/// Only the main block-processing thread reads from and writes to these caches. Correctness across
/// chain mutations is maintained by the consumer (the world-state scope wrapper):
/// <list type="bullet">
/// <item>On a reorg the scope's base block no longer matches <see cref="LastCommittedBlockNumber"/>,
/// so the caches are cleared before reuse.</item>
/// <item>A scope that is disposed without committing (e.g. an invalid block) clears the caches so
/// speculatively-written entries cannot leak into the next block.</item>
/// <item>CREATE/SELFDESTRUCT that wipe an account's storage clear the storage cache at commit.</item>
/// </list>
/// <see cref="SeqlockCache{TKey,TValue}.Clear"/> is an O(1) epoch bump, so invalidation is cheap.
/// </remarks>
public sealed class CrossBlockCaches
{
    // Sized larger than the per-block PreBlockCaches: a block's storage working set (read-only
    // SLOAD slots especially) is large, and cross-block reuse only pays off if hot slots survive
    // to the next block instead of being evicted. 16 -> 65536 sets x 2 ways = 131072 entries.
    private const int CrossBlockSetsLog2 = 16;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new(CrossBlockSetsLog2);
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new(CrossBlockSetsLog2);
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;

    /// <summary>
    /// Block number of the last successfully committed block. Used to detect reorgs: if the next
    /// scope's base block does not match this, the caches are stale and must be cleared.
    /// </summary>
    public long LastCommittedBlockNumber
    {
        get => Volatile.Read(ref _lastCommittedBlockNumber);
        set => Volatile.Write(ref _lastCommittedBlockNumber, value);
    }

    /// <summary>Clears both cross-block caches (O(1) epoch bump each).</summary>
    public void Clear()
    {
        _storageCache.Clear();
        _stateCache.Clear();
    }
}
