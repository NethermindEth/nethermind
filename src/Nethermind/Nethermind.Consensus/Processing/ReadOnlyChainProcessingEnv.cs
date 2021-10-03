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

using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Processing
{
    /// <summary>
    /// Not thread safe.
    /// </summary>
    public class ReadOnlyChainProcessingEnv : IDisposable
    {
        private readonly ReadOnlyTxProcessingEnv _txEnv;
        
        private readonly BlockchainProcessor _blockProcessingQueue;
        public IBlockProcessor BlockProcessor { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IBlockProcessingQueue BlockProcessingQueue { get; }
        public IStateProvider StateProvider => _txEnv.StateProvider;

        public ReadOnlyChainProcessingEnv(
            ReadOnlyTxProcessingEnv txEnv,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _txEnv = txEnv;

            BlockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                new BlockProcessor.BlockValidationTransactionsExecutor(_txEnv.TransactionProcessor, StateProvider),
                StateProvider,
                _txEnv.StorageProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);
            
            _blockProcessingQueue = new BlockchainProcessor(_txEnv.BlockTree, BlockProcessor, recoveryStep, logManager, BlockchainProcessor.Options.NoReceipts);
            BlockProcessingQueue = _blockProcessingQueue;
            ChainProcessor = new OneTimeChainProcessor(dbProvider, _blockProcessingQueue);
        }

        public void Dispose()
        {
            _blockProcessingQueue?.Dispose();
        }
    }
}
