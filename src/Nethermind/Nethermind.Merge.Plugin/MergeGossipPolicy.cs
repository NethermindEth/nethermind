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
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin
{
    public class MergeGossipPolicy : IGossipPolicy
    {
        private readonly IGossipPolicy _preMergeGossipPolicy;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IManualBlockFinalizationManager _blockFinalizationManager;

        public MergeGossipPolicy(
            IGossipPolicy? apiGossipPolicy, 
            IPoSSwitcher? poSSwitcher, 
            IManualBlockFinalizationManager blockFinalizationManager)
        {
            _preMergeGossipPolicy = apiGossipPolicy ?? throw new ArgumentNullException(nameof(apiGossipPolicy));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _blockFinalizationManager = blockFinalizationManager;
        }

        public bool ShouldGossipBlocks => !_poSSwitcher.HasEverReachedTerminalTotalDifficulty() && _preMergeGossipPolicy.ShouldGossipBlocks;

        public bool ShouldDisconnectGossipingNodes => _blockFinalizationManager.LastFinalizedHash != Keccak.Zero && _preMergeGossipPolicy.ShouldDisconnectGossipingNodes;

    }
}
