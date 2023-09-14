// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class StandardDbInitializerTests
    {
        private string _folderWithDbs;

        [OneTimeSetUp]
        public void Initialize()
        {
            _folderWithDbs = Guid.NewGuid().ToString();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_MemDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Mem, "mem");
            Type receiptsType = GetReceiptsType(useReceipts, typeof(MemColumnsDb<ReceiptsColumns>));
            AssertStandardDbs(dbProvider, typeof(MemDb), receiptsType);
            dbProvider.StateDb.Should().BeOfType(typeof(FullPruningDb));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_RocksDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Persisted, $"rocks_{useReceipts}");
            Type receiptsType = GetReceiptsType(useReceipts);
            AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
            dbProvider.StateDb.Should().BeOfType(typeof(FullPruningDb));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_ReadonlyDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Persisted, $"readonly_{useReceipts}");
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
            IDbProvider dbProvider = await InitializeStandardDb(false, DbModeHint.Mem, "pruning");
            dbProvider.StateDb.Should().BeOfType<FullPruningDb>();
        }

        private async Task<IDbProvider> InitializeStandardDb(bool useReceipts, DbModeHint dbModeHint, string path)
        {
            using IDbProvider dbProvider = new DbProvider(dbModeHint);
            RocksDbFactory rocksDbFactory = new(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, path));
            StandardDbInitializer initializer = new(dbProvider, rocksDbFactory, new MemDbFactory(), Substitute.For<IFileSystem>());
            await initializer.InitStandardDbsAsync(useReceipts);
            return dbProvider;
        }

        private static Type GetReceiptsType(bool useReceipts, Type receiptType = null) => useReceipts ? receiptType ?? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);

        private void AssertStandardDbs(IDbProvider dbProvider, Type dbType, Type receiptsDb)
        {
            dbProvider.BlockInfosDb.Should().BeOfType(dbType);
            dbProvider.BlocksDb.Should().BeOfType(dbType);
            dbProvider.BloomDb.Should().BeOfType(dbType);
            dbProvider.ChtDb.Should().BeOfType(dbType);
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
