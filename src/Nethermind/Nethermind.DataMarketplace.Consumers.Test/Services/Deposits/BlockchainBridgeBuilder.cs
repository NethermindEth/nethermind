// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.DataMarketplace.Consumers.Test.Services.Deposits
{
    public static class BlockchainBridgeBuilder
    {
        public static INdmBlockchainBridge BuildABridge()
        {
            IDbProvider memDbProvider = TestMemDbProvider.Init();
            StateReader stateReader = new StateReader(
                new TrieStore(memDbProvider.StateDb, LimboLogs.Instance), memDbProvider.CodeDb, LimboLogs.Instance);
            var trieStore = new TrieStore(memDbProvider.StateDb, LimboLogs.Instance);
            StateProvider stateProvider = new StateProvider(trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
            IEthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
            MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(MainnetSpecProvider.Instance, blockTree);
            ITxPool txPool = new TxPool.TxPool(
                ecdsa,
                new ChainHeadInfoProvider(specProvider, blockTree, stateProvider),
                new TxPoolConfig(),
                new TxValidator(specProvider.ChainId),
                LimboLogs.Instance,
                transactionComparerProvider.GetDefaultComparer());
            IWallet wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
            ReceiptsRecovery receiptsRecovery = new ReceiptsRecovery(ecdsa, specProvider);
            LogFinder logFinder = new LogFinder(blockTree, new InMemoryReceiptStorage(), NullBloomStorage.Instance,
                LimboLogs.Instance, receiptsRecovery, 1024);

            ReadOnlyTxProcessingEnv processingEnv = new ReadOnlyTxProcessingEnv(
                new ReadOnlyDbProvider(memDbProvider, false),
                new TrieStore(memDbProvider.StateDb, LimboLogs.Instance).AsReadOnly(memDbProvider.StateDb),
                new ReadOnlyBlockTree(blockTree),
                specProvider, LimboLogs.Instance);
            BlockchainBridge blockchainBridge = new BlockchainBridge(
                processingEnv,
                txPool,
                new InMemoryReceiptStorage(),
                NullFilterStore.Instance,
                NullFilterManager.Instance,
                ecdsa,
                Timestamper.Default,
                logFinder,
                specProvider,
                false);

            WalletTxSigner txSigner = new WalletTxSigner(wallet, ChainId.Mainnet);
            ITxSealer txSealer0 = new TxSealer(txSigner, Timestamper.Default);
            ITxSealer txSealer1 = new NonceReservingTxSealer(txSigner, Timestamper.Default, txPool);
            ITxSender txSender = new TxPoolSender(txPool, txSealer0, txSealer1);
            return new NdmBlockchainBridge(blockchainBridge, blockTree, stateReader, txSender);
        }
    }
}
