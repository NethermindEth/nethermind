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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Evm.Tracing.ParityStyle;
using System.Threading;
using System;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Trie.Pruning;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityStyleTracerTests
    {
        private BlockchainProcessor _processor;
        private BlockTree _blockTree;
        private Tracer _tracer;
        private IJsonRpcConfig _jsonRpcConfig = new JsonRpcConfig();

        [SetUp]
        public void Setup()
        {
            IDb blocksDb = new MemDb();
            IDb blocksInfoDb = new MemDb();
            IDb headersDb = new MemDb();
            ChainLevelInfoRepository repository = new ChainLevelInfoRepository(blocksInfoDb);
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            _blockTree = new BlockTree(blocksDb, headersDb, blocksInfoDb, repository, specProvider, NullBloomStorage.Instance, new SyncConfig(), LimboLogs.Instance);

            MemDb stateDb = new MemDb();
            MemDb codeDb = new MemDb();
            ITrieStore trieStore = new TrieStore(stateDb, LimboLogs.Instance).AsReadOnly();
            StateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, LimboLogs.Instance);

            BlockhashProvider blockhashProvider = new BlockhashProvider(_blockTree, LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, LimboLogs.Instance);
            TransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);
            
            BlockProcessor blockProcessor = new BlockProcessor(
                specProvider,
                Always.Valid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                storageProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                LimboLogs.Instance);

            var txRecovery = new RecoverSignatures(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), NullTxPool.Instance, specProvider, LimboLogs.Instance);
            _processor = new BlockchainProcessor(_blockTree, blockProcessor, txRecovery, LimboLogs.Instance, BlockchainProcessor.Options.NoReceipts);

            Block genesis = Build.A.Block.Genesis.TestObject;
            _blockTree.SuggestBlock(genesis);
            _processor.Process(genesis, ProcessingOptions.None, NullBlockTracer.Instance);
            
            _tracer = new Tracer(stateProvider, _processor);
        }

        [Test]
        public void Can_trace_raw_parity_style()
        {
            TraceRpcModule traceRpcModule = new TraceRpcModule(NullReceiptStorage.Instance, _tracer, _blockTree, _jsonRpcConfig);
            ResultWrapper<ParityTxTraceFromReplay> result = traceRpcModule.trace_rawTransaction(Bytes.FromHexString("f889808609184e72a00082271094000000000000000000000000000000000000000080a47f74657374320000000000000000000000000000000000000000000000000000006000571ca08a8bbf888cfa37bbf0bb965423625641fc956967b81d12e23709cead01446075a01ce999b56a8a88504be365442ea61239198e23d1fce7d00fcfc5cd3b44b7215f"), new[] {"trace"});
            Assert.NotNull(result.Data);
        }
        
        [Test]
        public void Can_trace_raw_parity_style_berlin_tx()
        {
            TraceRpcModule traceRpcModule = new TraceRpcModule(NullReceiptStorage.Instance, _tracer, _blockTree, _jsonRpcConfig);
            ResultWrapper<ParityTxTraceFromReplay> result = traceRpcModule.trace_rawTransaction(Bytes.FromHexString("01f85b821e8e8204d7847735940083030d408080853a60005500c080a0f43e70c79190701347517e283ef63753f6143a5225cbb500b14d98eadfb7616ba070893923d8a1fc97499f426524f9e82f8e0322dfac7c3d7e8a9eee515f0bcdc4"), new[] {"trace"});
            Assert.NotNull(result.Data);
        }
    }
}
