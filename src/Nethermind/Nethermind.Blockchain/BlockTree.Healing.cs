// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Repositories;

namespace Nethermind.Blockchain
{
    public partial class BlockTree : IBlockTreeHealer
    {
        // Cap per-flush memory of the upward scan at a few MB (~50-200 B per entry).
        private const int ClearStaleMarkersFlushThreshold = 65_536;

        public void HealCanonicalChain(Hash256 startHash, long maxBlockDepth)
        {
            BlockHeader? start = FindHeader(startHash, BlockTreeLookupOptions.None);
            if (start is null) return;

            using BatchWrite batch = _chainLevelInfoRepository.StartBatch();

            long repairedAbove = ClearStaleMarkersAbove(start.Number, batch);
            long repairedBelow = RepairMarkersBelow(start, maxBlockDepth, batch);

            if (Logger.IsInfo) Logger.Info($"Canonical chain heal complete: {repairedAbove + repairedBelow} level(s) repaired ({repairedAbove} stale above head cleared, {repairedBelow} incorrect markers fixed).");
        }

        private long ClearStaleMarkersAbove(long fromExclusive, BatchWrite batch)
        {
            // Cap at the highest level we could have written — a corrupted DB must not drive an unbounded scan.
            long upperBound = Math.Max(BestKnownNumber, BestKnownBeaconNumber);
            long cleared = 0L;
            int writesSinceFlush = 0;
            for (long levelNumber = fromExclusive + 1; levelNumber <= upperBound; levelNumber++)
            {
                ChainLevelInfo? level = LoadLevel(levelNumber);
                if (level is null) break;
                if (level.HasBlockOnMainChain)
                {
                    level.HasBlockOnMainChain = false;
                    _chainLevelInfoRepository.PersistLevel(levelNumber, level, batch);
                    cleared++;

                    if (++writesSinceFlush >= ClearStaleMarkersFlushThreshold)
                    {
                        batch.Flush();
                        writesSinceFlush = 0;
                    }
                }
            }
            return cleared;
        }

        private long RepairMarkersBelow(BlockHeader start, long maxBlockDepth, BatchWrite batch)
        {
            long repairedCount = 0L;
            long blocksWalked = 0L;
            BlockHeader current = start;

            while (blocksWalked <= maxBlockDepth)
            {
                ChainLevelInfo? level = LoadLevel(current.Number);
                if (level is not null)
                {
                    int? index = level.FindIndex(current.Hash!);
                    if (index is null)
                    {
                        if (Logger.IsWarn) Logger.Warn($"Canonical heal: block {current.Hash} at height {current.Number} not found in any BlockInfo slot — repair halted.");
                        break;
                    }

                    bool needsRepair = index.Value != 0 || !level.HasBlockOnMainChain;

                    if (index.Value != 0)
                        level.SwapToMain(index.Value);

                    level.HasBlockOnMainChain = true;

                    if (needsRepair)
                    {
                        _chainLevelInfoRepository.PersistLevel(current.Number, level, batch);
                        repairedCount++;
                    }
                }

                if (current.IsGenesis) break;

                BlockHeader? parent = FindHeader(current.ParentHash!, BlockTreeLookupOptions.None);
                if (parent is null)
                {
                    if (Logger.IsWarn) Logger.Warn($"Canonical heal: parent {current.ParentHash} of block {current.Number} not found — chain may be pruned, repair halted.");
                    break;
                }

                current = parent;
                blocksWalked++;
            }

            return repairedCount;
        }
    }
}
