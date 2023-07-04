// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            ReadOnlyDbProvider dbProvider = new(new DbProvider(DbModeHint.Mem), localChanges);
            dbProvider.ClearTempChanges();
        }
    }
}
