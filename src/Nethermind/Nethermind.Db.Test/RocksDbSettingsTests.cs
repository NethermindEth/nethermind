// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class RocksDbSettingsTests
    {
        [Test]
        public void clone_test()
        {
            DbSettings settings = new("name", "path")
            {
            };

            DbSettings settings2 = settings.Clone("Name2", "Path2");
            Assert.Multiple(() =>
            {
                Assert.That(settings2.DeleteOnStart, Is.EqualTo(settings.DeleteOnStart));
                Assert.That(settings2.CanDeleteFolder, Is.EqualTo(settings.CanDeleteFolder));
                Assert.That(settings2.MergeOperator, Is.EqualTo(settings.MergeOperator));
                Assert.That(settings2.ColumnsMergeOperators, Is.EqualTo(settings.ColumnsMergeOperators));
            });
            Assert.That(settings2.DbName, Is.EqualTo("Name2"));
            Assert.That(settings2.DbPath, Is.EqualTo("Path2"));
        }
    }
}
