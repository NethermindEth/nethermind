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
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class DbProviderTests
    {
        [Test]
        public void DbProvider_CanRegisterMemDb()
        {
            var memDbFactory = new MemDbFactory();
            using (var dbProvider = new DbProvider(DbModeHint.Mem))
            {
                var memDb = memDbFactory.CreateDb("MemDb");
                dbProvider.RegisterDb("MemDb", memDb);
                var db = dbProvider.GetDb<IDb>("MemDb");
                Assert.AreEqual(memDb, db);
            }
        }

        [Test]
        public void DbProvider_CanRegisterColumnsDb()
        {
            using (var dbProvider = new DbProvider(DbModeHint.Mem))
            {
                var memDbFactory = new MemDbFactory();
                var memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                var columnsDb = dbProvider.GetDb<IColumnsDb<ReceiptsColumns>>("ColumnsDb");
                Assert.AreEqual(memSnapshotableDb, columnsDb);
                Assert.IsTrue(memSnapshotableDb is IColumnsDb<ReceiptsColumns>);
            }
        }

        [Test]
        public void DbProvider_ThrowExceptionOnRegisteringTheSameDb()
        {
            using (var dbProvider = new DbProvider(DbModeHint.Mem))
            {
                var memDbFactory = new MemDbFactory();
                var memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                Assert.Throws<ArgumentException>(() => dbProvider.RegisterDb("columnsdb", new MemDb()));
            }
        }

        [Test]
        public void DbProvider_ThrowExceptionOnGettingNotRegisteredDb()
        {
            using (var dbProvider = new DbProvider(DbModeHint.Mem))
            {
                var memDbFactory = new MemDbFactory();
                var memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                Assert.Throws<ArgumentException>(() => dbProvider.GetDb<IColumnsDb<ReceiptsColumns>>("differentdb"));
            }
        }
    }
}
