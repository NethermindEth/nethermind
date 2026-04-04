// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block cache for storage slots. Survives across blocks, updated via write-through
/// and seeded from trie reads. Only the main processing thread reads/writes it.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<StorageCell, byte[], LargeCacheSets> _storageCache = new();
    private long _lastCommittedBlockNumber = -1;

    public SeqlockCache<StorageCell, byte[], LargeCacheSets> StorageCache => _storageCache;

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
