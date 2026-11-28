// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class ReadOnlyDbProviderTests
    {
        [Test]
        public void Can_clear([Values] bool localChanges)
        {
            MemDb wrappedDb = new();
            wrappedDb.Set(TestItem.KeccakA, [1, 2, 3]);
            IDbProvider wrappedProvider = Substitute.For<IDbProvider>();
            wrappedProvider.GetDb<IDb>("test").Returns(wrappedDb);

            ReadOnlyDbProvider dbProvider = new(wrappedProvider, localChanges);
            IDb readOnlyDb = dbProvider.GetDb<IDb>("test");

            if (localChanges)
            {
                readOnlyDb.Set(TestItem.KeccakB, [4, 5, 6]);
                Assert.That(readOnlyDb.Get(TestItem.KeccakB), Is.EqualTo(new byte[] { 4, 5, 6 }));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => readOnlyDb.Set(TestItem.KeccakB, [4, 5, 6]));
            }

            dbProvider.ClearTempChanges();

            using (Assert.EnterMultipleScope())
            {
                if (localChanges)
                {
                    Assert.That(readOnlyDb.Get(TestItem.KeccakB), Is.Null, "the in-memory overlay must be dropped");
                }

                Assert.That(readOnlyDb.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 1, 2, 3 }), "the wrapped db must be untouched");
            }
        }

        [Test]
        public void Clears_temporary_column_writes_without_changing_the_wrapped_database()
        {
            using MemColumnsDb<ReceiptsColumns> wrappedDb = new();
            IDb original = wrappedDb.GetColumnDb(ReceiptsColumns.Default);
            original.Set(TestItem.KeccakA, [1]);
            IDbProvider wrappedProvider = Substitute.For<IDbProvider>();
            wrappedProvider.GetColumnDb<ReceiptsColumns>(DbNames.Receipts).Returns(wrappedDb);
            using ReadOnlyDbProvider dbProvider = new(wrappedProvider, createInMemoryWriteStore: true);
            IDb readOnly = dbProvider.GetColumnDb<ReceiptsColumns>(DbNames.Receipts).GetColumnDb(ReceiptsColumns.Default);
            readOnly.Set(TestItem.KeccakB, [2]);
            readOnly.Set(TestItem.KeccakA, [3]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(readOnly.Get(TestItem.KeccakB), Is.EqualTo(new byte[] { 2 }));
                Assert.That(readOnly.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 3 }));
            }

            dbProvider.ClearTempChanges();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(readOnly.Get(TestItem.KeccakB), Is.Null);
                Assert.That(readOnly.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 1 }));
                Assert.That(original.Get(TestItem.KeccakA), Is.EqualTo(new byte[] { 1 }));
            }
        }
    }
}
