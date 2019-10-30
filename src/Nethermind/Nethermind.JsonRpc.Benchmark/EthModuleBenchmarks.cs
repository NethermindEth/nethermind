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

using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Benchmark
{
    [MemoryDiagnoser]
    public class EthModuleBenchmarks
    {
        private IVirtualMachine _virtualMachine;
        private IBlockhashProvider _blockhashProvider;
        private EthModule _ethModule;

        [GlobalSetup]
        public void GlobalSetup()
        {
            ISnapshotableDb codeDb = new StateDb();
            ISnapshotableDb stateDb = new StateDb();
            IDb blockInfoDb = new MemDb(10, 5);

            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            IReleaseSpec spec = MainNetSpecProvider.Instance.GenesisSpec;
            
            StateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            stateProvider.Commit(spec);

            StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, LimboLogs.Instance);
            StateReader stateReader = new StateReader(stateDb, codeDb, LimboLogs.Instance);
            
            ChainLevelInfoRepository chainLevelInfoRepository = new ChainLevelInfoRepository(blockInfoDb);
            BlockTree blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, chainLevelInfoRepository, specProvider, NullTxPool.Instance, LimboLogs.Instance);
            _blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            _virtualMachine = new VirtualMachine(stateProvider, storageProvider, _blockhashProvider, specProvider, LimboLogs.Instance);

            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            blockTree.SuggestBlock(genesisBlock);
            
            Block block1 = Build.A.Block.WithParent(genesisBlock).WithNumber(1).TestObject;
            blockTree.SuggestBlock(block1);
            
            TransactionProcessor transactionProcessor
                 = new TransactionProcessor(MainNetSpecProvider.Instance, stateProvider, storageProvider, _virtualMachine, LimboLogs.Instance);
            
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, AlwaysValidBlockValidator.Instance, new RewardCalculator(specProvider), transactionProcessor,
                stateDb, codeDb, new MemDb(), stateProvider, storageProvider, NullTxPool.Instance, NullReceiptStorage.Instance, LimboLogs.Instance);

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                new TxSignaturesRecoveryStep(new EthereumEcdsa(specProvider, LimboLogs.Instance), NullTxPool.Instance, LimboLogs.Instance),
                LimboLogs.Instance,
                false,
                false);

            blockchainProcessor.Process(genesisBlock, ProcessingOptions.None, NullBlockTracer.Instance);
            blockchainProcessor.Process(block1, ProcessingOptions.None, NullBlockTracer.Instance);

            BlockchainBridge bridge = new BlockchainBridge(
                stateReader,
                stateProvider,
                storageProvider,
                blockTree,
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                NullFilterStore.Instance,
                NullFilterManager.Instance, new DevWallet(new WalletConfig(), LimboLogs.Instance), transactionProcessor, new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance));
            
            _ethModule = new EthModule(LimboLogs.Instance, bridge);
        }

        [Benchmark]
        public void Current()
        {
            _ethModule.eth_getBalance(Address.Zero, new BlockParameter(1));
            _ethModule.eth_getBlockByNumber(new BlockParameter(1), false);
        }
    }
}