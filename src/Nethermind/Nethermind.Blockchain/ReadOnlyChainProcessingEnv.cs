/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class ReadOnlyChainProcessingEnv
    {
        private readonly ReadOnlyTxProcessingEnv _txEnv;
        public IBlockchainProcessor Processor { get; }
        public IStateProvider StateProvider => _txEnv.StateProvider;

        public ReadOnlyChainProcessingEnv(
            ReadOnlyTxProcessingEnv txEnv,
            IBlockValidator blockValidator,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _txEnv = txEnv;
            IBlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, _txEnv.TransactionProcessor, dbProvider.StateDb, dbProvider.CodeDb, dbProvider.TraceDb, StateProvider, _txEnv.StorageProvider, NullTxPool.Instance, receiptStorage, logManager);
            Processor = new OneTimeChainProcessor(dbProvider, new BlockchainProcessor(_txEnv.BlockTree, blockProcessor, recoveryStep, logManager, false, false));
        }
    }
}