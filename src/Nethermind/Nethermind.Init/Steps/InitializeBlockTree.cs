// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.HistoryPruning;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitTxTypesAndRlp), typeof(InitDatabase), typeof(MigrateConfigs), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IBasicApi _get;
        private readonly IApiWithStores _set;

        public InitializeBlockTree(INethermindApi api)
        {
            (_get, _set) = api.ForInit;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IInitConfig initConfig = _get.Config<IInitConfig>();
            IBloomConfig bloomConfig = _get.Config<IBloomConfig>();
            IHistoryConfig historyConfig = _get.Config<IHistoryConfig>();
            IBlocksConfig blocksConfig = _get.Config<IBlocksConfig>();

            ILogManager logManager = _get.LogManager;

            IFileStoreFactory fileStoreFactory = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? new InMemoryDictionaryFileStoreFactory()
                : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom, Bloom.ByteLength);

            IBloomStorage bloomStorage =
                _set.BloomStorage = bloomConfig.Index
                    ? new BloomStorage(bloomConfig, _get.DbProvider!.BloomDb, fileStoreFactory)
                    : NullBloomStorage.Instance;

            _get.DisposeStack.Push(bloomStorage);

            IChainLevelInfoRepository chainLevelInfoRepository =
                _set.ChainLevelInfoRepository = new ChainLevelInfoRepository(_get.DbProvider!.BlockInfosDb);

            IBlockStore blockStore = new BlockStore(_get.DbProvider.BlocksDb);
            IHeaderStore headerStore = new HeaderStore(_get.DbProvider.HeadersDb, _get.DbProvider.BlockNumbersDb);
            IBadBlockStore badBlockStore = _set.BadBlocksStore = new BadBlockStore(_get.DbProvider.BadBlocksDb, initConfig.BadBlocksStored ?? 100);

            IBlockTree blockTree = _set.BlockTree = new BlockTree(
                blockStore,
                headerStore,
                _get.DbProvider.BlockInfosDb,
                _get.DbProvider.MetadataDb,
                badBlockStore,
                chainLevelInfoRepository,
                _get.SpecProvider,
                bloomStorage,
                _get.Config<ISyncConfig>(),
                logManager);

            ISigner signer = NullSigner.Instance;
            ISignerStore signerStore = NullSigner.Instance;
            if (_get.Config<IMiningConfig>().Enabled)
            {
                Signer signerAndStore = new(_get.SpecProvider!.ChainId, _get.OriginalSignerKey!, logManager);
                signer = signerAndStore;
                signerStore = signerAndStore;
            }

            _set.EngineSigner = signer;
            _set.EngineSignerStore = signerStore;

            IReceiptConfig receiptConfig = _set.Config<IReceiptConfig>();
            ReceiptsRecovery receiptsRecovery = new(_get.EthereumEcdsa, _get.SpecProvider, !receiptConfig.CompactReceiptStore);
            IReceiptStorage receiptStorage = _set.ReceiptStorage = receiptConfig.StoreReceipts
                ? new PersistentReceiptStorage(
                    _get.DbProvider.ReceiptsDb,
                    _get.SpecProvider!,
                    receiptsRecovery,
                    blockTree,
                    blockStore,
                    receiptConfig,
                    new ReceiptArrayStorageDecoder(receiptConfig.CompactReceiptStore))
                : NullReceiptStorage.Instance;

            IReceiptFinder receiptFinder = _set.ReceiptFinder = new FullInfoReceiptFinder(receiptStorage, receiptsRecovery, blockTree);

            LogFinder logFinder = new(
                blockTree,
                receiptFinder,
                receiptStorage,
                bloomStorage,
                logManager,
                new ReceiptsRecovery(_get.EthereumEcdsa, _get.SpecProvider),
                receiptConfig.MaxBlockDepth);

            _set.LogFinder = logFinder;

            if (initConfig.ExitOnBlockNumber is not null)
            {
                _ = new ExitOnBlockNumberHandler(blockTree, _get.ProcessExit!, initConfig.ExitOnBlockNumber.Value, _get.LogManager);
            }

            if (historyConfig.Enabled)
            {
                HistoryPruner historyPruner = new(
                    blockTree,
                    receiptStorage,
                    _get.SpecProvider!,
                    blockStore,
                    chainLevelInfoRepository,
                    historyConfig,
                    (long)blocksConfig.SecondsPerSlot,
                    logManager);
                historyPruner.CheckConfig();
                _set.HistoryPruner = historyPruner;

                // blockchainProcessor.ProcessingQueueEmpty += historyPruner.OnBlockProcessorQueueEmpty;
            }

            return Task.CompletedTask;
        }
    }
}
