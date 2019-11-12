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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class TracerTests
    {
        private BlockchainProcessor _processor;
        private BlockTree _blockTree;

        [SetUp]
        public void Setup()
        {
            IDb blocksDb = new MemDb();
            IDb blocksInfoDb = new MemDb();
            IDb headersDb = new MemDb();
            ChainLevelInfoRepository repository = new ChainLevelInfoRepository(blocksInfoDb);
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            _blockTree = new BlockTree(blocksDb, headersDb, blocksInfoDb, repository, specProvider, NullTxPool.Instance, new SyncConfig(), LimboLogs.Instance);
            
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            StateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, LimboLogs.Instance);
            
            BlockhashProvider blockhashProvider = new BlockhashProvider(_blockTree, LimboLogs.Instance);
            
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, LimboLogs.Instance);
            
            TransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, TestBlockValidator.AlwaysValid, NoBlockRewards.Instance, transactionProcessor, stateDb, codeDb, new MemDb(), stateProvider, storageProvider, NullTxPool.Instance, NullReceiptStorage.Instance, LimboLogs.Instance);
            
            _processor = new BlockchainProcessor(_blockTree, blockProcessor, new CompositeDataRecoveryStep(), LimboLogs.Instance, false, false);
            Block genesis = Build.A.Block.Genesis.TestObject;
            _blockTree.SuggestBlock(genesis);
            _processor.Process(genesis, ProcessingOptions.None, NullBlockTracer.Instance);
        }

        [Test]
        public void Can_trace_raw_parity_style()
        {
            Tracer tracer = new Tracer(_processor, NullReceiptStorage.Instance, _blockTree, new MemDb());
            ParityLikeTxTrace result = tracer.ParityTraceRawTransaction(Bytes.FromHexString("f889808609184e72a00082271094000000000000000000000000000000000000000080a47f74657374320000000000000000000000000000000000000000000000000000006000571ca08a8bbf888cfa37bbf0bb965423625641fc956967b81d12e23709cead01446075a01ce999b56a8a88504be365442ea61239198e23d1fce7d00fcfc5cd3b44b7215f"), ParityTraceTypes.Trace);
            Assert.AreEqual(1L, result.BlockNumber);
        }
    }
}