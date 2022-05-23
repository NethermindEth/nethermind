//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin
{
    public class MergeGossipPolicy : IGossipPolicy
    {
        private readonly IGossipPolicy _preMergeGossipPolicy;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeGossipPolicy(
            IGossipPolicy? apiGossipPolicy,
            IPoSSwitcher? poSSwitcher)
        {
            _preMergeGossipPolicy = apiGossipPolicy ?? throw new ArgumentNullException(nameof(apiGossipPolicy));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        }

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We SHOULD NOT advertise the descendant of any terminal PoW block.
        // In our approach we CAN gossip blocks if we didn't received FIRST_FINALIZED_BLOCK yet...
        public bool CanGossipBlocks => !_poSSwitcher.TransitionFinished && _preMergeGossipPolicy.CanGossipBlocks;

        //  ...and gossipping policy will be decided according to the header difficulty.
        public bool ShouldGossipBlock(BlockHeader header) => !_poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge;
        
        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We MUST discard NewBlock/NewBlockHash messages after receiving FIRST_FINALIZED_BLOCK.
        public bool ShouldDiscardBlocks => _poSSwitcher.TransitionFinished;

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // We SHOULD start disconnecting gossiping peers after receiving next finalized block to FIRST_FINALIZED_BLOCK, so one block after we started discarding NewBlock/NewBlockHash messages.
        // In our approach we will start disconnecting peers when FinalTotalDifficulty will be set in config, so after first post-merge release.
        public bool ShouldDisconnectGossipingNodes => _poSSwitcher.FinalTotalDifficulty != null;
    }
}
