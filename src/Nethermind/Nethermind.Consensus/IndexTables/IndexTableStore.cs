// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// In-memory implementation of <see cref="IIndexTableStore"/> with per-level ring buffer eviction.
/// </summary>
/// <remarks>
/// Each level maintains up to <see cref="Eip8304Constants.TablesPerLevel"/> tables.
/// When the limit is exceeded, the table with the smallest first block at that level
/// is evicted. This implementation is thread-safe for concurrent reads and writes
/// but does not persist across node restarts (missing entries can be recovered historically
/// from block headers and receipts; a persistent database store can replace this for production use).
/// </remarks>
public class IndexTableStore : IIndexTableStore
{
    private readonly ConcurrentDictionary<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>>[] _entries;
    private readonly ConcurrentDictionary<(int Level, long FirstBlock), Hash256?>[] _latestByBlock;

    public IndexTableStore()
    {
        int numLevels = Eip8304Constants.TableSizes.Length;
        _entries = new ConcurrentDictionary<(int, long, Hash256?), IReadOnlyList<IndexEntry>>[numLevels];
        _latestByBlock = new ConcurrentDictionary<(int, long), Hash256?>[numLevels];
        for (int i = 0; i < numLevels; i++)
        {
            _entries[i] = new ConcurrentDictionary<(int, long, Hash256?), IReadOnlyList<IndexEntry>>();
            _latestByBlock[i] = new ConcurrentDictionary<(int, long), Hash256?>();
        }
    }

    /// <inheritdoc />
    public void Store(int level, long firstBlock, IReadOnlyList<IndexEntry> sortedEntries, Hash256? blockHash = null)
    {
        ConcurrentDictionary<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> dict = _entries[level];
        ConcurrentDictionary<(int Level, long FirstBlock), Hash256?> latestDict = _latestByBlock[level];

        dict[(level, firstBlock, blockHash)] = sortedEntries;
        latestDict[(level, firstBlock)] = blockHash;

        // Evict oldest if ring buffer full
        if (latestDict.Count > Eip8304Constants.TablesPerLevel)
        {
            long minBlock = long.MaxValue;
            foreach (KeyValuePair<(int Level, long FirstBlock), Hash256?> kvp in latestDict)
            {
                if (kvp.Key.FirstBlock < minBlock)
                    minBlock = kvp.Key.FirstBlock;
            }

            if (latestDict.TryRemove((level, minBlock), out _))
            {
                foreach (KeyValuePair<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> kvp in dict)
                {
                    if (kvp.Key.Level == level && kvp.Key.FirstBlock == minBlock)
                    {
                        dict.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IndexEntry>? Get(int level, long firstBlock, Hash256? blockHash = null)
    {
        ConcurrentDictionary<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> dict = _entries[level];

        if (blockHash is not null)
        {
            return dict.TryGetValue((level, firstBlock, blockHash), out IReadOnlyList<IndexEntry>? branchEntries)
                ? branchEntries
                : null;
        }

        if (_latestByBlock[level].TryGetValue((level, firstBlock), out Hash256? latestHash))
        {
            if (dict.TryGetValue((level, firstBlock, latestHash), out IReadOnlyList<IndexEntry>? latestEntries))
                return latestEntries;
        }

        dict.TryGetValue((level, firstBlock, null), out IReadOnlyList<IndexEntry>? fallbackEntries);
        return fallbackEntries;
    }

    /// <inheritdoc />
    public void Remove(int level, long firstBlock, Hash256? blockHash)
    {
        ConcurrentDictionary<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> dict = _entries[level];

        if (blockHash is not null)
        {
            dict.TryRemove((level, firstBlock, blockHash), out _);
            if (_latestByBlock[level].TryGetValue((level, firstBlock), out Hash256? latest) && latest == blockHash)
            {
                _latestByBlock[level].TryRemove((level, firstBlock), out _);
            }
            return;
        }

        _latestByBlock[level].TryRemove((level, firstBlock), out _);
        foreach (KeyValuePair<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> kvp in dict)
        {
            if (kvp.Key.Level == level && kvp.Key.FirstBlock == firstBlock)
            {
                dict.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public void Remove(int level, long firstBlock) => Remove(level, firstBlock, null);

    /// <inheritdoc />
    public void InvalidateAbove(long blockNumber)
    {
        for (int level = 0; level < _entries.Length; level++)
        {
            // A level-i table covers [firstBlock, firstBlock + TABLE_SIZES[i] - 1], so it is
            // invalidated as soon as its last covered block is above the retained head.
            long lastBlockOffset = Eip8304Constants.TableSizes[level] - 1;
            ConcurrentDictionary<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> dict = _entries[level];
            foreach (KeyValuePair<(int Level, long FirstBlock, Hash256? BlockHash), IReadOnlyList<IndexEntry>> kvp in dict)
            {
                if (kvp.Key.FirstBlock + lastBlockOffset > blockNumber)
                {
                    dict.TryRemove(kvp.Key, out _);
                }
            }

            ConcurrentDictionary<(int Level, long FirstBlock), Hash256?> latestDict = _latestByBlock[level];
            foreach (KeyValuePair<(int Level, long FirstBlock), Hash256?> kvp in latestDict)
            {
                if (kvp.Key.FirstBlock + lastBlockOffset > blockNumber)
                {
                    latestDict.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
