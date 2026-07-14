// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.TxPool;

namespace Nethermind.Init.Modules;

public class BlockTreeModule(IReceiptConfig receiptConfig, ILogIndexConfig logIndexConfig) : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IHeaderStore, HeaderStore>()
            .AddSingleton<IHeaderFinder>(c => c.Resolve<IHeaderStore>())
            .AddSingleton<IBlockStore, IDb, IHeaderDecoder, IDeferredBlockDataWriter, IStatePersistenceBarrier>(CreateBlockStore)
            .AddSingleton<IDeferredBlockDataWriter>(CreateDeferredWriter)
            .AddSingleton<IReceiptMigrationStore, PersistentReceiptStorage>()
            .Bind<IReceiptStorage, IReceiptMigrationStore>()
            .AddSingleton<IBadBlockStore, IDb, IInitConfig>(CreateBadBlockStore)
            .AddSingleton<IBlockAccessListStore, IDb, IDeferredBlockDataWriter, IStatePersistenceBarrier>(CreateBalStore)
            .AddSingleton<IChainLevelInfoRepository, ChainLevelInfoRepository>()
            .AddSingleton<IBlobTxStorage, BlobTxStorage>()
            .AddSingleton<IReceiptsRecovery, IEthereumEcdsa, ISpecProvider, IReceiptConfig>((ecdsa, specProvider, receiptConfig) =>
                new ReceiptsRecovery(ecdsa, specProvider, !receiptConfig.CompactReceiptStore)
            )
            .AddSingleton<IReceiptFinder, FullInfoReceiptFinder>()
            .AddSingleton<IHistoryPruner, HistoryPruner>()
            .AddSingleton<IBlockTree, BlockTree>()
            .Bind<IBlockFinder, IBlockTree>()
            .AddSingleton<IBlockTreeHealer, IBlockTree>((bt) => (IBlockTreeHealer)bt)
            .AddSingleton<IReadOnlyBlockTree, IBlockTree>((bt) => bt.AsReadOnly());

        builder.AddSingleton<ILogIndexBuilder, LogIndexBuilder>()
            .AddDecorator<ILogIndexConfig>((ctx, config) =>
            {
                IPruningConfig pruningConfig = ctx.Resolve<IPruningConfig>();
                config.MaxReorgDepth ??= pruningConfig.PruningBoundary;
                return config;
            });

        if (logIndexConfig.Enabled)
        {
            builder
                .AddSingleton<ILogIndexStorage, LogIndexStorage>()
                .AddSingleton<ILogFinder, IndexedLogFinder>();
        }
        else
        {
            builder
                .AddSingleton<ILogIndexStorage, DisabledLogIndexStorage>()
                .AddSingleton<ILogFinder, LogFinder>();
        }

        if (!receiptConfig.StoreReceipts)
        {
            builder.AddSingleton<IReceiptMigrationStore>(NullReceiptStorage.Instance);
        }
    }

    // Activate the DBs the writer targets before the writer itself, so Autofac's reverse-activation-order
    // disposal drains the writer (flushing queued writes) before it closes those DBs.
    private IDeferredBlockDataWriter CreateDeferredWriter(IComponentContext ctx)
    {
        if (receiptConfig.DeferredPersistence)
        {
            ctx.ResolveKeyed<IDb>(DbNames.Blocks);
            ctx.ResolveKeyed<IDb>(DbNames.BlockAccessLists);
            ctx.Resolve<IColumnsDb<ReceiptsColumns>>();
        }
        return new DeferredBlockDataWriter(receiptConfig.DeferredPersistence, receiptConfig.MaxDeferredWrites, ctx.Resolve<ILogManager>(), ctx.Resolve<IStatePersistenceBarrier>());
    }

    private IBlockStore CreateBlockStore([KeyFilter(DbNames.Blocks)] IDb blocksDb, IHeaderDecoder headerDecoder, IDeferredBlockDataWriter deferredWriter, IStatePersistenceBarrier persistenceBarrier) =>
        new BlockStore(blocksDb, headerDecoder, deferredWriter: deferredWriter, persistenceBarrier: persistenceBarrier);

    private IBadBlockStore CreateBadBlockStore([KeyFilter(DbNames.BadBlocks)] IDb badBlockDb, IInitConfig initConfig) =>
        new BadBlockStore(badBlockDb, initConfig.BadBlocksStored ?? 100);

    private IBlockAccessListStore CreateBalStore([KeyFilter(DbNames.BlockAccessLists)] IDb balDb, IDeferredBlockDataWriter deferredWriter, IStatePersistenceBarrier persistenceBarrier) =>
        new BlockAccessListStore(balDb, deferredWriter: deferredWriter, persistenceBarrier: persistenceBarrier);
}
