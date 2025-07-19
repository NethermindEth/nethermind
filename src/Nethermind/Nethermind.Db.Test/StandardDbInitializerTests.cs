// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class StandardDbInitializerTests
    {
        private string _folderWithDbs = null!;

        [OneTimeSetUp]
        public void Initialize()
        {
            _folderWithDbs = Guid.NewGuid().ToString();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_MemDbProvider(bool useReceipts)
        {
            using IDbProvider dbProvider = await InitializeStandardDb(useReceipts, true, "mem");
            Type receiptsType = GetReceiptsType(useReceipts, typeof(MemColumnsDb<ReceiptsColumns>));
            AssertStandardDbs(dbProvider, typeof(MemDb), receiptsType);
            dbProvider.StateDb.Should().BeOfType(typeof(FullPruningDb));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_RocksDbProvider(bool useReceipts)
        {
            using IDbProvider dbProvider = await InitializeStandardDb(useReceipts, false, $"rocks_{useReceipts}");
            Type receiptsType = GetReceiptsType(useReceipts);
            AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
            dbProvider.StateDb.Should().BeOfType(typeof(FullPruningDb));
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
            dbProvider.StateDb.Should().BeOfType(typeof(FullPruningDb));
            ((IDbProvider)readonlyDbProvider).StateDb.Should().BeOfType(typeof(ReadOnlyDb));
        }

        [Test]
        public async Task InitializerTests_WithPruning()
        {
            using IDbProvider dbProvider = await InitializeStandardDb(false, true, "pruning");
            dbProvider.StateDb.Should().BeOfType<FullPruningDb>();
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
                .AddModule(new WorldStateModule(initConfig)) // For the full pruning db
                .AddSingleton<IDbConfig>(new DbConfig())
                .AddSingleton<IInitConfig>(initConfig)
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IFileSystem, FileSystem>()
                .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
                .Build();

            return Task.FromResult(container.Resolve<IDbProvider>());
        }

        private static Type GetReceiptsType(bool useReceipts, Type receiptType = null) => useReceipts ? receiptType ?? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);

        private void AssertStandardDbs(IDbProvider dbProvider, Type dbType, Type receiptsDb)
        {
            dbProvider.BlockInfosDb.Should().BeOfType(dbType);
            dbProvider.BlocksDb.Should().BeOfType(dbType);
            dbProvider.BloomDb.Should().BeOfType(dbType);
            dbProvider.HeadersDb.Should().BeOfType(dbType);
            dbProvider.ReceiptsDb.Should().BeOfType(receiptsDb);
            dbProvider.CodeDb.Should().BeOfType(dbType);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
