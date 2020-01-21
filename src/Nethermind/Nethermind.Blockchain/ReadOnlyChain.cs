//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    [Todo("Review how it compares with ReadOnlyChainProcessingEnv")]
    public class ReadOnlyChain
    {
        public IBlockchainProcessor Processor { get; }
        public IStateProvider ReadOnlyStateProvider { get; }
        public IEnumerable<IAdditionalBlockProcessor> AdditionalBlockProcessors { get; }
        public IBlockProcessor BlockProcessor { get; }

        public ReadOnlyChain(ReadOnlyBlockTree readOnlyTree,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ISpecProvider specProvider,
            IReadOnlyDbProvider dbProvider,
            IBlockDataRecoveryStep recoveryStep,
            ILogManager logManager,
            ITxPool customTxPool,
            IReceiptStorage receiptStorage,
            Func<IDb, IStateProvider, IBlockTree, ITransactionProcessor, ILogManager, IEnumerable<IAdditionalBlockProcessor>> additionalBlockProcessorsFactory)
        {
            ReadOnlyStateProvider = new StateProvider(dbProvider.StateDb, dbProvider.CodeDb, logManager);
            StorageProvider storageProvider = new StorageProvider(dbProvider.StateDb, ReadOnlyStateProvider, logManager);
            BlockhashProvider blockhashProvider = new BlockhashProvider(readOnlyTree, logManager);
            VirtualMachine virtualMachine = new VirtualMachine(ReadOnlyStateProvider, storageProvider, blockhashProvider, specProvider, logManager);
            ITransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, ReadOnlyStateProvider, storageProvider, virtualMachine, logManager);
            ITxPool txPool = customTxPool;
            AdditionalBlockProcessors = additionalBlockProcessorsFactory?.Invoke(dbProvider.StateDb, ReadOnlyStateProvider, readOnlyTree, transactionProcessor, logManager);
            BlockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider.StateDb, dbProvider.CodeDb, ReadOnlyStateProvider, storageProvider, txPool, receiptStorage, logManager, AdditionalBlockProcessors);
            Processor = new OneTimeChainProcessor(dbProvider, new BlockchainProcessor(readOnlyTree, BlockProcessor, recoveryStep, logManager, false));
        }
    }
}