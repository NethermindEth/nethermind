// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block cache for storage slots. Survives across blocks and is updated via
/// write-through during block commit. Unlike <see cref="PreBlockCaches"/>, this is
/// never shared with the prewarmer — only the main processing thread reads/writes it.
/// </summary>
/// <remarks>
/// Account (state) caching is intentionally excluded. The base scope (flat state) must be
/// the single source of truth for Account values because the EVM reads the account from
/// the scope and writes modified values back through the same scope's write batch. Returning
/// a cached Account that doesn't match the base scope's version causes InvalidStateRoot.
/// Storage slot values are safe to cache because they are independent leaf values with no
/// structural coupling to the trie.
/// </remarks>
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
