// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class RocksDbSettingsTests
    {
        [Test]
        public void clone_test()
        {
            RocksDbSettings settings = new("name", "path")
            {
                BlockCacheSize = 1,
                UpdateReadMetrics = () => { },
                UpdateWriteMetrics = () => { },
                WriteBufferNumber = 5,
                WriteBufferSize = 10,
                CacheIndexAndFilterBlocks = true
            };

            RocksDbSettings settings2 = settings.Clone("Name2", "Path2");
            settings2.Should().BeEquivalentTo(settings,
                o => o.Excluding(s => s.DbName).Excluding(s => s.DbPath));
            settings2.DbName.Should().Be("Name2");
            settings2.DbPath.Should().Be("Path2");
        }
    }
}
