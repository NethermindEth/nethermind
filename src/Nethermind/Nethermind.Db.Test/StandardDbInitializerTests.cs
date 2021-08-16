//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
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
            using IDbProvider dbProvider = new DbProvider(DbModeHint.Mem);
            RocksDbFactory rocksDbFactory = new(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, "mem"));
            StandardDbInitializer initializer = new(dbProvider, rocksDbFactory, new MemDbFactory());
            await initializer.InitStandardDbsAsync(useReceipts);
            Type receiptsType = useReceipts ? typeof(MemColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);
            AssertStandardDbs(dbProvider, typeof(MemDb), receiptsType);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_RocksDbProvider(bool useReceipts)
        {
            using (IDbProvider dbProvider = new DbProvider(DbModeHint.Persisted))
            {
                RocksDbFactory rocksDbFactory = new(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, $"rocks_{useReceipts}"));
                StandardDbInitializer initializer = new(dbProvider, rocksDbFactory, new MemDbFactory());
                await initializer.InitStandardDbsAsync(useReceipts);
                Type receiptsType = useReceipts ? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);
                AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_ReadonlyDbProvider(bool useReceipts)
        {
            using (IDbProvider dbProvider = new DbProvider(DbModeHint.Persisted))
            {
                RocksDbFactory rocksDbFactory = new(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, $"readonly_{useReceipts}"));
                StandardDbInitializer initializer = new(dbProvider, rocksDbFactory, new MemDbFactory());
                await initializer.InitStandardDbsAsync(useReceipts);
                using (ReadOnlyDbProvider readonlyDbProvider = new(dbProvider, true))
                {
                    Type receiptsType = useReceipts ? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);
                    AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
                    AssertStandardDbs(readonlyDbProvider, typeof(ReadOnlyDb), typeof(ReadOnlyColumnsDb<ReceiptsColumns>));
                }
            }
        }
        
        private void AssertStandardDbs(IDbProvider dbProvider, Type dbType, Type receiptsDb)
        {
            Assert.IsTrue(dbProvider.BlockInfosDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.BlocksDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.BloomDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.ChtDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.HeadersDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.ReceiptsDb.GetType() == receiptsDb);
            Assert.IsTrue(dbProvider.CodeDb.GetType() == dbType);
            Assert.IsTrue(dbProvider.StateDb.GetType() == dbType);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
