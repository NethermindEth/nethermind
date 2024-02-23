// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(MigrateConfigs), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IBasicApi _get;
        private readonly IApiWithStores _set;

        public InitializeBlockTree(INethermindApi api)
        {
            (_get, _set) = api.ForInit;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
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
            IBlockStore badBlockStore = _set.BadBlocksStore = new BlockStore(_get.DbProvider.BadBlocksDb, initConfig.BadBlocksStored);

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
                _get.LogManager);

            ISigner signer = NullSigner.Instance;
            ISignerStore signerStore = NullSigner.Instance;
            IMiningConfig miningConfig = _get.Config<IMiningConfig>();
            if (miningConfig.Enabled)
            {
                if (!string.IsNullOrEmpty(miningConfig.Signer))
                {
                    ClefSigner signerAndStore =
                        await SetupExternalSigner(miningConfig.Signer, _get.SpecProvider!.ChainId, _get.Config<IKeyStoreConfig>().BlockAuthorAccount);
                    signer = signerAndStore;
                    signerStore = signerAndStore;
                }
                else
                {
                    Signer signerAndStore = new Signer(_get.SpecProvider!.ChainId, _get.OriginalSignerKey!, _get.LogManager);
                    signer = signerAndStore;
                    signerStore = signerAndStore;
                }
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

            if (initConfig.ExitOnBlockNumber is not null)
            {
                new ExitOnBlockNumberHandler(blockTree, _get.ProcessExit!, initConfig.ExitOnBlockNumber.Value, _get.LogManager);
            }

        }

        private async Task<ClefSigner> SetupExternalSigner(string urlSigner, ulong chainId, string blockAuthorAccount)
        {
            try
            {
                Address? address = string.IsNullOrEmpty(blockAuthorAccount) ? null : new Address(blockAuthorAccount);
                BasicJsonRpcClient rpcClient = new(new Uri(urlSigner), _get.EthereumJsonSerializer, _get.LogManager, TimeSpan.FromSeconds(10));
                _get.DisposeStack.Push(rpcClient);
                return await ClefSigner.Create(rpcClient, chainId, address);
            }
            catch (HttpRequestException e)
            {
                throw new NetworkingException($"Remote signer at {urlSigner} did not respond.", NetworkExceptionType.TargetUnreachable, e);
            }
        }
    }
}
