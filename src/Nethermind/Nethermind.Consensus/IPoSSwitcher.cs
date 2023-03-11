// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus
{
    public interface IPoSSwitcher
    {
        void ForkchoiceUpdated(BlockHeader newHeadHash, Keccak finalizedHash);

        bool HasEverReachedTerminalBlock();

        event EventHandler TerminalBlockReached;

        UInt256? TerminalTotalDifficulty { get; }

        /// <summary>
        /// Total difficulty is total difficulty of the last PoW block.
        /// </summary>
        /// <remarks>
        /// FinalTotalDifficulty >= TerminalTotalDifficulty.
        /// TerminalTotalDifficulty is trigger for transition process. However, the last PoW block will be bigger than TTD.
        /// Thanks to this variable, we can simplify many things in our code. For example, we can insert newPayload with FinalTotalDifficulty
        /// This value will be known after the merge transition, and we can configure it in the first release after the merge.
        /// </remarks>
        UInt256? FinalTotalDifficulty { get; }

        bool TransitionFinished { get; }
        public Keccak ConfiguredTerminalBlockHash { get; }

        public long? ConfiguredTerminalBlockNumber { get; }

        // We can get TerminalBlock from three different points in the system:
        // 1) Block Processing - it is needed because we need to switch classes, for example, block production, during the transition
        // 2) forkchoice - it will handle reorgs in terminal blocks during the transition process
        // 3) reverse header sync - we need to find the terminal block to process blocks correctly
        // Note: In the first post-merge release, the terminal block will be known, it explains why we can override it through settings.
        bool TryUpdateTerminalBlock(BlockHeader header);

        (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header);

        bool IsPostMerge(BlockHeader header);
    }

    public static class PoSSwitcherExtensions
    {
        public static bool MisconfiguredTerminalTotalDifficulty(this IPoSSwitcher poSSwitcher) => poSSwitcher.TerminalTotalDifficulty is null;

        public static bool BlockBeforeTerminalTotalDifficulty(this IPoSSwitcher poSSwitcher, BlockHeader blockHeader) => blockHeader.TotalDifficulty < poSSwitcher.TerminalTotalDifficulty;
    }
}
