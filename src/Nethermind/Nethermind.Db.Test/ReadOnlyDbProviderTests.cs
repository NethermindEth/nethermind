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
        [TestCase(true)]
        [TestCase(false)]
        public void Can_clear(bool localChanges)
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
    }
}
