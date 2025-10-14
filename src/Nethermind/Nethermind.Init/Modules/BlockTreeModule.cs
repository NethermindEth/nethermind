// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using Nethermind.History;
using Nethermind.State.Repositories;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class BlockTreeModule(IReceiptConfig receiptConfig, ILogIndexConfig logIndexConfig) : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddKeyedSingleton<IFileStoreFactory>(nameof(BloomStorage), CreateBloomStorageFileStoreFactory)
            .AddSingleton<IBloomStorage, BloomStorage>()
            .AddSingleton<IHeaderStore, HeaderStore>()
            .AddSingleton<IBlockStore, BlockStore>()
            .AddSingleton<IReceiptStorage, PersistentReceiptStorage>()
            .AddSingleton<IBadBlockStore, IDb, IInitConfig>(CreateBadBlockStore)
            .AddSingleton<IChainLevelInfoRepository, ChainLevelInfoRepository>()
            .AddSingleton<IBlobTxStorage, BlobTxStorage>()
            .AddSingleton<IReceiptsRecovery, IEthereumEcdsa, ISpecProvider, IReceiptConfig>((ecdsa, specProvider, receiptConfig) =>
                new ReceiptsRecovery(ecdsa, specProvider, !receiptConfig.CompactReceiptStore)
            )
            .AddSingleton<IReceiptFinder, FullInfoReceiptFinder>()
            .AddSingleton<IHistoryPruner, HistoryPruner>()

            .AddSingleton<IBlockTree, BlockTree>()
            .Bind<IBlockFinder, IBlockTree>()
            .AddSingleton<ILogFinder, LogFinder>()
            .AddSingleton<IReadOnlyBlockTree, IBlockTree>((bt) => bt.AsReadOnly());

        builder.AddSingleton<ILogIndexBuilder, LogIndexBuilder>();
        if (logIndexConfig.Enabled)
        {
            builder.AddSingleton<ILogIndexStorage, LogIndexStorage>();
        }
        else
        {
            builder.AddSingleton<ILogIndexStorage, DisabledLogIndexStorage>();
        }

        if (!receiptConfig.StoreReceipts)
        {
            builder.AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance);
        }
    }

    private IFileStoreFactory CreateBloomStorageFileStoreFactory(IComponentContext ctx)
    {
        IInitConfig initConfig = ctx.Resolve<IInitConfig>();
        return initConfig.DiagnosticMode == DiagnosticMode.MemDb
            ? new InMemoryDictionaryFileStoreFactory()
            : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom,
                Bloom.ByteLength);
    }

    private IBadBlockStore CreateBadBlockStore([KeyFilter(DbNames.BadBlocks)] IDb badBlockDb, IInitConfig initConfig)
    {
        return new BadBlockStore(badBlockDb, initConfig.BadBlocksStored ?? 100);
    }
}
