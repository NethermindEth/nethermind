// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Repositories
{
    public interface IChainLevelInfoRepository
    {
        void Delete(ulong number, BatchWrite? batch = null);
        void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null);
        BatchWrite StartBatch();
        ChainLevelInfo? LoadLevel(ulong number);

        /// <summary>Loads a level as <see cref="LoadLevel"/> does, without caching a level read from the database.</summary>
        /// <remarks>For long scans of historical levels, which would otherwise evict the recent levels from the cache.</remarks>
        ChainLevelInfo? LoadLevelWithoutCaching(ulong number) => LoadLevel(number);

        /// <summary>Reads the main chain block's hash and total difficulty as <see cref="LoadLevelWithoutCaching"/> does; <see langword="false"/> when the level has no main chain block.</summary>
        bool TryGetMainChainBlockWithoutCaching(ulong number, out ValueHash256 blockHash, out UInt256 totalDifficulty) =>
            TryGetMainChainBlock(LoadLevelWithoutCaching(number), out blockHash, out totalDifficulty);

        /// <summary>Reads the main chain block's hash and total difficulty of a decoded level; <see langword="false"/> when it has none.</summary>
        internal static bool TryGetMainChainBlock(ChainLevelInfo? level, out ValueHash256 blockHash, out UInt256 totalDifficulty)
        {
            if (level?.MainChainBlock is { } block)
            {
                blockHash = block.BlockHash.ValueHash256;
                totalDifficulty = block.TotalDifficulty;
                return true;
            }

            blockHash = default;
            totalDifficulty = default;
            return false;
        }

        IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers);

        /// <summary>Loads each level as <see cref="LoadLevelWithoutCaching"/> does, in request order.</summary>
        IOwnedReadOnlyList<ChainLevelInfo?> LoadLevelsWithoutCaching(in ArrayPoolListRef<ulong> blockNumbers)
        {
            ArrayPoolList<ChainLevelInfo?> levels = new(blockNumbers.Count, blockNumbers.Count);
            for (int i = 0; i < blockNumbers.Count; i++) levels[i] = LoadLevelWithoutCaching(blockNumbers[i]);
            return levels;
        }
    }
}
