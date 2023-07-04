// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class MergeGossipPolicy : IGossipPolicy
    {
        private readonly IGossipPolicy _preMergeGossipPolicy;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IBlockCacheService _blockCacheService;

        public MergeGossipPolicy(
            IGossipPolicy? apiGossipPolicy,
            IPoSSwitcher? poSSwitcher,
            IBlockCacheService blockCacheService)
        {
            _preMergeGossipPolicy = apiGossipPolicy ?? throw new ArgumentNullException(nameof(apiGossipPolicy));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _blockCacheService = blockCacheService ?? throw new ArgumentNullException(nameof(blockCacheService));
        }

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We SHOULD NOT advertise the descendant of any terminal PoW block.
        // In our approach we CAN gossip blocks if we didn't received FIRST_FINALIZED_BLOCK yet...
        public bool CanGossipBlocks => !_poSSwitcher.TransitionFinished && _preMergeGossipPolicy.CanGossipBlocks;

        //  ...and gossipping policy will be decided according to the header difficulty.
        public bool ShouldGossipBlock(BlockHeader header) => !_poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge;

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We MUST discard NewBlock/NewBlockHash messages after receiving FIRST_FINALIZED_BLOCK.
        public bool ShouldDiscardBlocks => _poSSwitcher.TransitionFinished ||
                                           _blockCacheService.FinalizedHash != Keccak.Zero;  /* _blockCacheService.FinalizedHash != Keccak.Zero - this condition was added for edge case situation.
                                            We started beacon sync, and we hadn't reached transition yet. If CL sent us non zero finalization hash, it would mean that network reached transition.
                                            However, in edge case situation (verified by merge hive tests), our node needs to be reorged to PoW again, so we can't add this condition _blockCacheService.FinalizedHash != Keccak.Zero
                                            to PoSSwitcher.TransitionFinished. On the other hand, we don't want to receive any blocks from the network, so we want to discard blocks. */

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We SHOULD start disconnecting gossiping peers after receiving next finalized block to FIRST_FINALIZED_BLOCK, so one block after we started discarding NewBlock/NewBlockHash messages.
        // In our approach we will start disconnecting peers when FinalTotalDifficulty will be set in config, so after first post-merge release.
        public bool ShouldDisconnectGossipingNodes => _poSSwitcher.FinalTotalDifficulty is not null;
    }
}
