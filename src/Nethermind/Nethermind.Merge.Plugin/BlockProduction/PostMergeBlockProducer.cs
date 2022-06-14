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
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducer : BlockProducerBase
    {
        public PostMergeBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager) 
            : base(
                txSource, 
                processor, 
                sealEngine, 
                blockTree, 
                blockProductionTrigger, 
                stateProvider, 
                gasLimitCalculator, 
                timestamper, 
                specProvider, 
                logManager,
                ConstantDifficulty.Zero)
        {
        }
        
        public Block PrepareEmptyBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = PrepareBlockHeader(parent, payloadAttributes);
            blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            blockHeader.TxRoot = Keccak.EmptyTreeHash;
            blockHeader.Bloom = Bloom.Empty;
            
            Block block = new(blockHeader, Array.Empty<Transaction>(), Array.Empty<BlockHeader>());

            // processing is only done here to apply block rewards in AuRa
            if (TrySetState(parent.StateRoot))
            {
                block = ProcessPreparedBlock(block, null) ?? block;
            }
            
            return block;
        }
        
        protected override Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = base.PrepareBlock(parent, payloadAttributes);
            AmendHeader(block.Header);
            return block;
        }

        protected override BlockHeader PrepareBlockHeader(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            BlockHeader blockHeader = base.PrepareBlockHeader(parent, payloadAttributes);
            AmendHeader(blockHeader);
            return blockHeader;
        }

        // TODO: this seems to me that it should be done in the Eth2 seal engine?
        private static void AmendHeader(BlockHeader blockHeader)
        {
            blockHeader.ExtraData = Array.Empty<byte>();
            blockHeader.IsPostMerge = true;
        }
    }
}
