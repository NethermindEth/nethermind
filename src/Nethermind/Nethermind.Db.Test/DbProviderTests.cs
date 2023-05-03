// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            MemDbFactory memDbFactory = new MemDbFactory();
            using (DbProvider dbProvider = new DbProvider(DbModeHint.Mem))
            {
                IDb memDb = memDbFactory.CreateDb("MemDb");
                dbProvider.RegisterDb("MemDb", memDb);
                IDb db = dbProvider.GetDb<IDb>("MemDb");
                Assert.That(db, Is.EqualTo(memDb));
            }
        }

        [Test]
        public void DbProvider_CanRegisterColumnsDb()
        {
            using (DbProvider dbProvider = new DbProvider(DbModeHint.Mem))
            {
                MemDbFactory memDbFactory = new MemDbFactory();
                IColumnsDb<ReceiptsColumns> memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                IColumnsDb<ReceiptsColumns> columnsDb = dbProvider.GetDb<IColumnsDb<ReceiptsColumns>>("ColumnsDb");
                Assert.That(columnsDb, Is.EqualTo(memSnapshotableDb));
                Assert.IsTrue(memSnapshotableDb is IColumnsDb<ReceiptsColumns>);
            }
        }

        [Test]
        public void DbProvider_ThrowExceptionOnRegisteringTheSameDb()
        {
            using (DbProvider dbProvider = new DbProvider(DbModeHint.Mem))
            {
                MemDbFactory memDbFactory = new MemDbFactory();
                IColumnsDb<ReceiptsColumns> memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                Assert.Throws<ArgumentException>(() => dbProvider.RegisterDb("columnsdb", new MemDb()));
            }
        }

        [Test]
        public void DbProvider_ThrowExceptionOnGettingNotRegisteredDb()
        {
            using (DbProvider dbProvider = new DbProvider(DbModeHint.Mem))
            {
                MemDbFactory memDbFactory = new MemDbFactory();
                IColumnsDb<ReceiptsColumns> memSnapshotableDb = memDbFactory.CreateColumnsDb<ReceiptsColumns>("ColumnsDb");
                dbProvider.RegisterDb("ColumnsDb", memSnapshotableDb);
                Assert.Throws<ArgumentException>(() => dbProvider.GetDb<IColumnsDb<ReceiptsColumns>>("differentdb"));
            }
        }
    }
}
