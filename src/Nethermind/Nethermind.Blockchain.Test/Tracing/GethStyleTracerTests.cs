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

using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing
{
    [TestFixture]
    public class GethStyleTracerTests
    {
        private BlockchainProcessor _processor;
        private BlockTree _blockTree;

        [SetUp]
        public void Setup()
        {
            IDb blocksDb = new MemDb();
            IDb headersDb = new MemDb();
            IDb blocksInfoDb = new MemDb();
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            
            ChainLevelInfoRepository repository = new ChainLevelInfoRepository(blocksInfoDb);
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            _blockTree = new BlockTree(blocksDb, headersDb, blocksInfoDb, repository, specProvider, NullTxPool.Instance, NullBloomStorage.Instance, new SyncConfig(), LimboLogs.Instance);
            
            StateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, LimboLogs.Instance);
            
            BlockhashProvider blockhashProvider = new BlockhashProvider(_blockTree, LimboLogs.Instance);
            
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, LimboLogs.Instance);
            
            TransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, TestBlockValidator.AlwaysValid, NoBlockRewards.Instance, transactionProcessor, stateDb, codeDb, stateProvider, storageProvider, NullTxPool.Instance, NullReceiptStorage.Instance, LimboLogs.Instance);
            
            _processor = new BlockchainProcessor(_blockTree, blockProcessor, new CompositeDataRecoveryStep(), LimboLogs.Instance, false);
            Block genesis = Build.A.Block.Genesis.TestObject;
            _blockTree.SuggestBlock(genesis);
            _processor.Process(genesis, ProcessingOptions.None, NullBlockTracer.Instance);
        }
    }
}