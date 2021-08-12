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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class Eth2BlockProducer : BlockProducerBase
    {
        public Eth2BlockProducer(ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            IStateProvider stateProvider,
            IGasLimitCalculator gasLimitCalculator,
            ISigner signer,
            ITimestamper timestamper,
            ISpecProvider specProvider,
            ILogManager logManager) 
            : base(
                txSource, 
                processor, 
                new Eth2SealEngine(signer), 
                blockTree, 
                blockProductionTrigger, 
                stateProvider, 
                gasLimitCalculator, 
                timestamper, 
                specProvider, 
                logManager,
                ConstantDifficultyCalculator.One)
        {
        }

        protected override Block PrepareBlock(BlockHeader parent)
        {
            Block block = base.PrepareBlock(parent);
            block.Header.MixHash = Keccak.Zero;
            block.Header.ExtraData = Bytes.Empty;
            return block;
        }
    }
}
