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
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Find;
using Nethermind.State.Repositories;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class BlockTreeModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddSingleton<IFileStoreFactory, IInitConfig>(CreateFileStoreFactory)
            .AddSingleton<IBloomStorage, BloomStorage>()
            .AddSingleton<IHeaderStore, HeaderStore>()
            .AddSingleton<IBlockStore, BlockStore>()
            .AddSingleton<IReceiptsRecovery, ReceiptsRecovery>()
            .AddSingleton<IReceiptStorage, PersistentReceiptStorage>()
            .Bind<IReceiptFinder, IReceiptStorage>()
            .AddSingleton<IBadBlockStore, IDb, IInitConfig>(CreateBadBlockStore)
            .AddSingleton<IChainLevelInfoRepository, ChainLevelInfoRepository>()
            .AddSingleton<IBlobTxStorage, IDbProvider, ITxPoolConfig>(CreateBlobTxStorage)
            .AddSingleton<IBlockTree, BlockTree>()
            .Bind<IBlockFinder, IBlockTree>()
            .AddSingleton<ILogFinder, LogFinder>()
            ;
    }

    private IBlobTxStorage CreateBlobTxStorage(IDbProvider dbProvider, ITxPoolConfig txPoolConfig)
    {
        bool useBlobsDb = txPoolConfig.BlobsSupport.IsPersistentStorage();
        return useBlobsDb
            ? new BlobTxStorage(dbProvider!.BlobTransactionsDb)
            : NullBlobTxStorage.Instance;
    }

    private IFileStoreFactory CreateFileStoreFactory(IInitConfig initConfig)
    {
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
