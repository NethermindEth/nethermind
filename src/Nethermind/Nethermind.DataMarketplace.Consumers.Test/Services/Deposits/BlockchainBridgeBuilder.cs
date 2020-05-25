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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public static class BlockchainBridgeBuilder
    {
        public static INdmBlockchainBridge BuildABridge()
        {
            MemDbProvider memDbProvider = new MemDbProvider();
            StateReader stateReader = new StateReader(memDbProvider.StateDb, memDbProvider.CodeDb, LimboLogs.Instance);
            StateProvider stateProvider = new StateProvider(memDbProvider.StateDb, memDbProvider.CodeDb, LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(memDbProvider.StateDb, stateProvider, LimboLogs.Instance);
            IEthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            ITxPool txPool = new TxPool.TxPool(new InMemoryTxStorage(), Timestamper.Default, ecdsa, MainnetSpecProvider.Instance, new TxPoolConfig(), stateProvider, LimboLogs.Instance);
            // BlockTree blockTree = new BlockTree(memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb), MainnetSpecProvider.Instance, txPool, NullBloomStorage.Instance, new SyncConfig(), LimboLogs.Instance);
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
            IWallet wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, new BlockhashProvider(blockTree, LimboLogs.Instance), MainnetSpecProvider.Instance, LimboLogs.Instance);
            TransactionProcessor processor = new TransactionProcessor(MainnetSpecProvider.Instance, stateProvider, storageProvider, virtualMachine, LimboLogs.Instance);

            BlockchainBridge blockchainBridge = new BlockchainBridge(stateReader, stateProvider, storageProvider, blockTree, txPool, new InMemoryReceiptStorage(), NullFilterStore.Instance, NullFilterManager.Instance, wallet, processor, ecdsa, NullBloomStorage.Instance, LimboLogs.Instance, false);
            TxPoolBridge txPoolBridge = new TxPoolBridge(txPool, wallet, Timestamper.Default, ChainId.Mainnet);
            return new NdmBlockchainBridge(txPoolBridge, blockchainBridge, txPool);
        }
    }
}