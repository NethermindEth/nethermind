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
        private readonly IManualBlockFinalizationManager _blockFinalizationManager;

        public MergeGossipPolicy(
            IGossipPolicy? apiGossipPolicy, 
            IManualBlockFinalizationManager blockFinalizationManager)
        {
            ShouldGossipBlocks = apiGossipPolicy?.ShouldGossipBlocks ?? throw new ArgumentNullException(nameof(apiGossipPolicy));
            _blockFinalizationManager = blockFinalizationManager;
            
            _blockFinalizationManager.BlocksFinalized += OnBlockFinalized;
        }

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // we MUST discard NewBlock/NewBlockHash messages after receiving FIRST_FINALIZED_BLOCK
        public bool ShouldDiscardBlocks { get; private set; }

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // we MUST stop gossiping peers after receiving next finalized block to FIRST_FINALIZED_BLOCK, so one block after we started discarding NewBlock/NewBlockHash messages
        public bool ShouldGossipBlocks { get; private set; }

        // According to spec (https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#network)
        // we SHOULD start disconnecting gossiping peers after receiving next finalized block to FIRST_FINALIZED_BLOCK, so one block after we started discarding NewBlock/NewBlockHash messages
        // for now hardcoded false - in spec it is SHOULD, not MUST, so we don't want to disconnect any peers during transition. Later just change to ShouldGossipBlocks
        public bool ShouldDisconnectGossipingNodes => false;    //=> ShouldGossipBlocks;

        private Keccak _firstFinalizedBlockHash = Keccak.Zero;
        
        private void OnBlockFinalized(object? sender, FinalizeEventArgs e)
        {
            Keccak finalizedBlockHash = e.FinalizedBlocks[0]?.Hash ?? Keccak.Zero;
            
            if (finalizedBlockHash == Keccak.Zero)
                return;

            if (_firstFinalizedBlockHash == Keccak.Zero)
            {
                _firstFinalizedBlockHash = finalizedBlockHash;
                ShouldDiscardBlocks = true;
            }
            else if (finalizedBlockHash != _firstFinalizedBlockHash)
            {
                ShouldGossipBlocks = false;
                _blockFinalizationManager.BlocksFinalized -= OnBlockFinalized;
            }
        }
    }
}
