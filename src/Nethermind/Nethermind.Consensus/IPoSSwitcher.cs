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
        void ForkchoiceUpdated(BlockHeader newHeadHash, Hash256 finalizedHash);

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
        public Hash256? ConfiguredTerminalBlockHash { get; }

        public ulong? ConfiguredTerminalBlockNumber { get; }

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

        /// <summary>
        /// Restores <see cref="BlockHeader.IsPostMerge"/> on <paramref name="callHeader"/>, the private header copy an RPC
        /// call or trace runs on, which skipped the preprocessing step that restores the flag for block processing.
        /// </summary>
        /// <remarks>
        /// A set flag is kept and a zero difficulty is post-merge on every chain; otherwise the chain's switcher
        /// decides, as it does for block processing (Taiko keeps a nonzero difficulty in post-merge headers). Block 0
        /// follows its difficulty alone, as in geth. The flag is written on the header passed in, by this method and
        /// by some switchers, so pass the call's own copy, before block overrides. Not a consensus rule: block
        /// processing must not use it.
        /// </remarks>
        public static void RestorePostMergeForCall(this IPoSSwitcher poSSwitcher, BlockHeader callHeader)
        {
            if (callHeader.IsGenesis)
                callHeader.IsPostMerge = callHeader.Difficulty.IsZero;
            else if (!callHeader.IsPostMerge)
                callHeader.IsPostMerge = callHeader.Difficulty.IsZero || poSSwitcher.IsPostMerge(callHeader);
        }
    }
}
