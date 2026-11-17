// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        /// <summary>Reads the main chain blocks at <paramref name="blockNumbers"/>, as <see cref="TryGetMainChainBlockWithoutCaching"/> does each one.</summary>
        /// <param name="blockNumbers">The numbers of the levels.</param>
        /// <param name="blocks">Receives the hash and total difficulty of each main chain block, or the default when the level has none.</param>
        void GetMainChainBlocksWithoutCaching(ReadOnlyMemory<ulong> blockNumbers, Memory<(ValueHash256 BlockHash, UInt256 TotalDifficulty)> blocks)
        {
            for (int i = 0; i < blockNumbers.Length; i++)
            {
                blocks.Span[i] = TryGetMainChainBlockWithoutCaching(blockNumbers.Span[i], out ValueHash256 blockHash, out UInt256 totalDifficulty)
                    ? (blockHash, totalDifficulty)
                    : default;
            }
        }

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

        /// <summary>Loads the levels of <paramref name="blockNumbers"/> in order, as <see cref="LoadLevelWithoutCaching"/> does; <see langword="null"/> for a missing level.</summary>
        /// <remarks>The levels not in the cache are read in one batch.</remarks>
        IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers);
    }
}
