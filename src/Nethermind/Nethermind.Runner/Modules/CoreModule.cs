// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.TxPool;
using Module = Autofac.Module;

namespace Nethermind.Runner.Modules;

public class CoreModule : Module
{
    private bool _isUsingMemdb;
    private bool _storeReceipts;
    private bool _isMining;
    private bool _persistentBlobTxStorages;
    private bool _indexBloom;

    public CoreModule(IConfigProvider configProvider)
    {
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        _isUsingMemdb = initConfig.DiagnosticMode == DiagnosticMode.MemDb;
        _storeReceipts = initConfig.StoreReceipts;
        _persistentBlobTxStorages = configProvider.GetConfig<ITxPoolConfig>().BlobsSupport.IsPersistentStorage();
        _isMining = configProvider.GetConfig<IMiningConfig>().Enabled;
        _indexBloom = configProvider.GetConfig<IBloomConfig>().Index;
    }

    public CoreModule(
        bool isUsingMemdb = true,
        bool storeReceipts = true,
        bool isMining = true,
        bool persistentBlobTxStorages = true,
        bool indexBloom = true
    )
    {
        // Used for testing
        _isUsingMemdb = isUsingMemdb;
        _storeReceipts = storeReceipts;
        _isMining = isMining;
        _persistentBlobTxStorages = persistentBlobTxStorages;
        _indexBloom = indexBloom;
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // TODO: Move to block processing module when it exist
        builder.RegisterImpl<FollowOtherMiners, IGasLimitCalculator>();
        builder.RegisterSingleton<NethermindApi, INethermindApi>();
        ConfigureSigner(builder);

        ConfigureBlobTxStore(builder);
        ConfigureBloom(builder);
        ConfigureReceipts(builder);

        builder.RegisterSingleton<ChainLevelInfoRepository, IChainLevelInfoRepository>();
        builder.RegisterType<BlockStore>()
            .WithParameter(ResolvedParameter.ForKeyed<IDb>(DbNames.Blocks))
            .Keyed<IBlockStore>(IBlockStore.Key.Main)
            .SingleInstance();

        builder.RegisterType<BlockStore>()
            .WithParameter(ResolvedParameter.ForKeyed<IDb>(DbNames.BadBlocks))
            .WithParameter(GetParameter.FromType<IInitConfig>(ParameterKey.BlockStoreMaxSize, cfg => cfg.BadBlocksStored))
            .Keyed<IBlockStore>(IBlockStore.Key.BadBlock)
            .SingleInstance();

        builder.RegisterSingleton<HeaderStore, IHeaderStore>();

        builder.RegisterType<BlockTree>()
            .WithAttributeFiltering()
            .SingleInstance()
            .As<IBlockTree>()
            .As<IBlockFinder>();
    }

    private void ConfigureSigner(ContainerBuilder builder)
    {
        builder.RegisterType<EthereumEcdsa>().AsImplementedInterfaces();
        if (_isMining)
        {
            builder.RegisterType<Signer>()
                .WithAttributeFiltering()
                .As<ISignerStore>()
                .As<ISigner>()
                .SingleInstance();
        }
        else
        {
            builder.RegisterInstance(NullSigner.Instance)
                .As<ISignerStore>()
                .As<ISigner>()
                .SingleInstance();
        }
    }

    private void ConfigureBlobTxStore(ContainerBuilder builder)
    {
        if (_persistentBlobTxStorages)
        {
            builder.RegisterImpl<BlobTxStorage, IBlobTxStorage>();
        }
        else
        {
            builder.RegisterInstance<IBlobTxStorage>(NullBlobTxStorage.Instance);
        }
    }

    private void ConfigureReceipts(ContainerBuilder builder)
    {
        if (_storeReceipts)
        {
            builder.RegisterKeyedMapping<IReceiptConfig, bool>(ComponentKey.UseCompactReceiptStore, conf => conf.CompactReceiptStore);
            builder.RegisterSingleton<PersistentReceiptStorage, IReceiptStorage>();
        }
        else
        {
            builder.RegisterInstance<IReceiptStorage>(NullReceiptStorage.Instance);
        }

        builder.RegisterImpl<FullInfoReceiptFinder, IReceiptFinder>();
        builder.RegisterType<ReceiptsRecovery>().As<IReceiptsRecovery>()
            .UsingConstructor(typeof(IEthereumEcdsa), typeof(ISpecProvider), typeof(IReceiptConfig));
        builder.RegisterImpl<LogFinder, ILogFinder>();
    }

    private void ConfigureBloom(ContainerBuilder builder)
    {
        if (_isUsingMemdb)
        {
            builder.RegisterImpl<InMemoryDictionaryFileStoreFactory, IFileStoreFactory>();
        }
        else
        {
            builder.Register<IInitConfig, IFileStoreFactory>((cfg)
                    => new FixedSizeFileStoreFactory(Path.Combine(cfg.BaseDbPath, DbNames.Bloom), DbNames.Bloom, Bloom.ByteLength))
                .SingleInstance();
        }

        builder.RegisterType<BloomStorage>().WithAttributeFiltering();

        if (_indexBloom)
        {
            builder.RegisterSingleton<BloomStorage, IBloomStorage>();
        }
        else
        {
            builder.RegisterInstance<IBloomStorage>(NullBloomStorage.Instance);
        }
    }
}
