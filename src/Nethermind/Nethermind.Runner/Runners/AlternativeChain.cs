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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Runner.Runners
{
    [Todo("This will be replaced with a bigger rewrite of state management so we can create a state at will")]
    internal class AlternativeChain
    {
        public IBlockchainProcessor Processor { get; }
        public IStateProvider StateProvider { get; }

        public AlternativeChain(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ISpecProvider specProvider,
            IReadOnlyDbProvider dbProvider,
            IBlockDataRecoveryStep recoveryStep,
            ILogManager logManager,
            ITxPool customTxPool,
            IReceiptStorage receiptStorage)
        {
            StateProvider = new StateProvider(dbProvider.StateDb, dbProvider.CodeDb, logManager);
            StorageProvider storageProvider = new StorageProvider(dbProvider.StateDb, StateProvider, logManager);
            IBlockTree readOnlyTree = new ReadOnlyBlockTree(blockTree);
            BlockhashProvider blockhashProvider = new BlockhashProvider(readOnlyTree, logManager);
            VirtualMachine virtualMachine = new VirtualMachine(StateProvider, storageProvider, blockhashProvider, logManager);
            ITransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, StateProvider, storageProvider, virtualMachine, logManager);
            ITxPool txPool = customTxPool;
            IBlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider.StateDb, dbProvider.CodeDb, dbProvider.TraceDb, StateProvider, storageProvider, txPool, receiptStorage, logManager);
            Processor = new OneTimeProcessor(dbProvider, new BlockchainProcessor(readOnlyTree, blockProcessor, recoveryStep, logManager, false, false));
        }
    }
}