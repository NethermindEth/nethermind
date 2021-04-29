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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Test
{
    internal class Eth2TestBlockProducerFactory : Eth2BlockProducerFactory
    {
        public override Eth2BlockProducer Create(
            IBlockTree blockTree,
            IDbProvider dbProvider,
            IReadOnlyTrieStore readOnlyTrieStore,
            IBlockPreprocessorStep blockPreprocessor,
            ITxPool txPool,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            IBlockProcessingQueue blockProcessingQueue,
            ISpecProvider specProvider, 
            ISigner engineSigner,
            IMiningConfig miningConfig,
            ILogManager logManager)
        {
            BlockProducerContext producerContext = GetProducerChain(
                blockTree,
                dbProvider,
                readOnlyTrieStore,
                blockPreprocessor,
                txPool,
                blockValidator, 
                rewardCalculatorSource, 
                receiptStorage,
                specProvider,
                miningConfig,
                logManager);
                
            return new Eth2TestBlockProducer(
                producerContext.TxSource,
                producerContext.ChainProcessor,
                blockTree,
                blockProcessingQueue,
                producerContext.ReadOnlyStateProvider,
                new TargetAdjustedGasLimitCalculator(specProvider, miningConfig),
                engineSigner,
                logManager);
        }
    }
}
