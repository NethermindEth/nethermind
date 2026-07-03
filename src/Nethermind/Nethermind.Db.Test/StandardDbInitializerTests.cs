// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
public class StandardDbInitializerTests
{
    private string _folderWithDbs = null!;

    [OneTimeSetUp]
    public void Initialize() => _folderWithDbs = Guid.NewGuid().ToString();

    [TestCase(false)]
    [TestCase(true)]
    public async Task InitializerTests_MemDbProvider(bool useReceipts)
    {
        using IDbProvider dbProvider = await InitializeStandardDb(useReceipts, true, "mem");
        Type receiptsType = GetReceiptsType(useReceipts, typeof(SnapshotableMemColumnsDb<ReceiptsColumns>));
        AssertStandardDbs(dbProvider, typeof(MemDb), receiptsType);
        Assert.That(dbProvider.StateDb, Is.TypeOf<FullPruningDb>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InitializerTests_RocksDbProvider(bool useReceipts)
    {
        using IDbProvider dbProvider = await InitializeStandardDb(useReceipts, false, $"rocks_{useReceipts}");
        Type receiptsType = GetReceiptsType(useReceipts);
        AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
        Assert.That(dbProvider.StateDb, Is.TypeOf<FullPruningDb>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InitializerTests_ReadonlyDbProvider(bool useReceipts)
    {
        using IDbProvider dbProvider = await InitializeStandardDb(useReceipts, false, $"readonly_{useReceipts}");
        using ReadOnlyDbProvider readonlyDbProvider = new(dbProvider, true);
        Type receiptsType = GetReceiptsType(useReceipts);
        AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
        AssertStandardDbs(readonlyDbProvider, typeof(ReadOnlyDb), GetReceiptsType(false));
        Assert.That(dbProvider.StateDb, Is.TypeOf<FullPruningDb>());
        Assert.That(((IDbProvider)readonlyDbProvider).StateDb, Is.TypeOf<ReadOnlyDb>());
    }

    [Test]
    public async Task InitializerTests_WithPruning()
    {
        using IDbProvider dbProvider = await InitializeStandardDb(false, true, "pruning");
        Assert.That(dbProvider.StateDb, Is.TypeOf<FullPruningDb>());
    }

    private Task<IDbProvider> InitializeStandardDb(bool useReceipts, bool useMemDb, string path)
    {
        IInitConfig initConfig = new InitConfig()
        {
            DiagnosticMode = useMemDb ? DiagnosticMode.MemDb : DiagnosticMode.None,
            BaseDbPath = path
        };

        IContainer container = new ContainerBuilder()
            .AddModule(new DbModule(initConfig, new ReceiptConfig()
            {
                StoreReceipts = useReceipts
            }, new SyncConfig()
            {
                DownloadReceiptsInFastSync = useReceipts
            }))
            .AddModule(new PruningTrieStoreModule()) // For the full pruning db
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IFileSystem, RealFileSystem>()
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo(1))
            .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
            .Build();

        return Task.FromResult(container.Resolve<IDbProvider>());
    }

    private static Type GetReceiptsType(bool useReceipts, Type receiptType = null) => useReceipts ? receiptType ?? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);

    private void AssertStandardDbs(IDbProvider dbProvider, Type dbType, Type receiptsDb)
    {
        AssertBlockDataDb(dbProvider.BlockInfosDb);
        AssertBlockDataDb(dbProvider.BlocksDb);
        AssertBlockDataDb(dbProvider.HeadersDb);
        Assert.That(dbProvider.ReceiptsDb, Is.TypeOf(receiptsDb));
        Assert.That(dbProvider.CodeDb, Is.TypeOf(dbType));

        void AssertBlockDataDb(IDb db)
        {
            if (dbType == typeof(ReadOnlyDb))
            {
                Assert.That(db, Is.TypeOf(dbType));
            }
            else
            {
                // Block-data dbs are registered behind the write-behind decorator; the factory type is inside.
                Assert.That(db, Is.TypeOf<WriteBehindDb>());
                Assert.That(((WriteBehindDb)db).Inner, Is.TypeOf(dbType));
            }
        }
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folderWithDbs))
            Directory.Delete(_folderWithDbs, true);
    }
}
