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
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevBlockProducerEnvFactory : BlockProducerEnvFactory
    {
        public MevBlockProducerEnvFactory(
            IDbProvider dbProvider, 
            IBlockTree blockTree, 
            IReadOnlyTrieStore readOnlyTrieStore,
            ISpecProvider specProvider, 
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptStorage,
            IBlockPreprocessorStep blockPreprocessorStep, 
            ITxPool txPool, 
            IMiningConfig miningConfig,
            ILogManager logManager) 
            : base(dbProvider, blockTree, readOnlyTrieStore, specProvider, blockValidator, rewardCalculatorSource, receiptStorage, blockPreprocessorStep, txPool, miningConfig, logManager)
        {
        }

        protected override BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider, 
            IBlockValidator blockValidator, 
            IRewardCalculatorSource rewardCalculatorSource, 
            IReceiptStorage receiptStorage,
            ILogManager logManager)
        {
            return LastMevBlockProcessor = new MevBlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                new MevProduceBlockTransactionsStrategy(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);
        }

        public MevBlockProcessor LastMevBlockProcessor { get; set; } = null!;
    }
}
