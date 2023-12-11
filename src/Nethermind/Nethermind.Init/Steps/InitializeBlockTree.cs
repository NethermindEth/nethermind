// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Era1;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(MigrateConfigs), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IBasicApi _get;
        private readonly IApiWithStores _set;
        private readonly INethermindApi _api;

        public InitializeBlockTree(INethermindApi api)
        {
            (_get, _set) = api.ForInit;
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            IInitConfig initConfig = _get.Config<IInitConfig>();
            IBloomConfig bloomConfig = _get.Config<IBloomConfig>();

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
            IEraStore? eraStore = SetupEraStore(initConfig, BlockchainIds.GetBlockchainName(_api.SpecProvider.NetworkId));
            IBlockTree blockTree = _set.BlockTree = new BlockTree(
                blockStore,
                headerStore,
                eraStore,
                _get.DbProvider.BlockInfosDb,
                _get.DbProvider.MetadataDb,
                chainLevelInfoRepository,
                _get.SpecProvider,
                bloomStorage,
                _get.Config<ISyncConfig>(),
                _get.LogManager);

            ISigner signer = NullSigner.Instance;
            ISignerStore signerStore = NullSigner.Instance;
            if (_get.Config<IMiningConfig>().Enabled)
            {
                Signer signerAndStore = new(_get.SpecProvider!.ChainId, _get.OriginalSignerKey!, _get.LogManager);
                signer = signerAndStore;
                signerStore = signerAndStore;
            }

            _set.EngineSigner = signer;
            _set.EngineSignerStore = signerStore;

            IReceiptConfig receiptConfig = _set.Config<IReceiptConfig>();
            ReceiptsRecovery receiptsRecovery = new(_get.EthereumEcdsa, _get.SpecProvider, !receiptConfig.CompactReceiptStore);
            IReceiptStorage receiptStorage = _set.ReceiptStorage = initConfig.StoreReceipts
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
                _get.LogManager,
                new ReceiptsRecovery(_get.EthereumEcdsa, _get.SpecProvider),
                receiptConfig.MaxBlockDepth);

            _set.LogFinder = logFinder;

            return Task.CompletedTask;
        }

        private IEraStore? SetupEraStore(IInitConfig initConfig, string networkName)
        {
            if (string.IsNullOrWhiteSpace(initConfig.AncientDataDirectory))
            {
                return null;
            }

            string[] erafiles =
                EraReader.GetAllEraFiles(initConfig.AncientDataDirectory, networkName.ToLower()).ToArray();

            if (!erafiles.Any())
                throw new InvalidConfigurationException($"The configured directory '{initConfig.AncientDataDirectory}' for ancient data contains no era1 files for {networkName}.", ExitCodes.GeneralError);

            return new EraStore(erafiles, _get.FileSystem);
        }
    }
}
