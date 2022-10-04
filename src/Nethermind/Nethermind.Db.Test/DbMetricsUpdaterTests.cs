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
using Nethermind.Db.Rocks.Statistics;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;

namespace Nethermind.Db.Test
{

    [NonParallelizable]
    public class DbMetricsUpdaterTests
    {
        [TearDown]
        public void TearDow()
        {
            Metrics.DbStats.Clear();
        }

        [Test]
        public void ProcessCompactionStats_AllDataExist()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_AllData.txt");
            new DbMetricsUpdater("Test", null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.AreEqual(11, Metrics.DbStats.Count);
            Assert.AreEqual(2, Metrics.DbStats["TestDbLevel0Files"]);
            Assert.AreEqual(0, Metrics.DbStats["TestDbLevel0FilesCompacted"]);
            Assert.AreEqual(4, Metrics.DbStats["TestDbLevel1Files"]);
            Assert.AreEqual(2, Metrics.DbStats["TestDbLevel1FilesCompacted"]);
            Assert.AreEqual(3, Metrics.DbStats["TestDbLevel2Files"]);
            Assert.AreEqual(1, Metrics.DbStats["TestDbLevel2FilesCompacted"]);
            Assert.AreEqual(10, Metrics.DbStats["TestDbIntervalCompactionGBWrite"]);
            Assert.AreEqual(2, Metrics.DbStats["TestDbIntervalCompactionMBPerSecWrite"]);
            Assert.AreEqual(123, Metrics.DbStats["TestDbIntervalCompactionGBRead"]);
            Assert.AreEqual(0, Metrics.DbStats["TestDbIntervalCompactionMBPerSecRead"]);
            Assert.AreEqual(111, Metrics.DbStats["TestDbIntervalCompactionSeconds"]);
        }

        [Test]
        public void ProcessCompactionStats_MissingLevels()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingLevels.txt");
            new DbMetricsUpdater("Test", null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.AreEqual(5, Metrics.DbStats.Count);
            Assert.AreEqual(10, Metrics.DbStats["TestDbIntervalCompactionGBWrite"]);
            Assert.AreEqual(2, Metrics.DbStats["TestDbIntervalCompactionMBPerSecWrite"]);
            Assert.AreEqual(123, Metrics.DbStats["TestDbIntervalCompactionGBRead"]);
            Assert.AreEqual(0, Metrics.DbStats["TestDbIntervalCompactionMBPerSecRead"]);
            Assert.AreEqual(111, Metrics.DbStats["TestDbIntervalCompactionSeconds"]);
        }

        [Test]
        public void ProcessCompactionStats_MissingIntervalCompaction_Warning()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingIntervalCompaction.txt");
            new DbMetricsUpdater("Test", null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.AreEqual(6, Metrics.DbStats.Count);
            Assert.AreEqual(2, Metrics.DbStats["TestDbLevel0Files"]);
            Assert.AreEqual(0, Metrics.DbStats["TestDbLevel0FilesCompacted"]);
            Assert.AreEqual(4, Metrics.DbStats["TestDbLevel1Files"]);
            Assert.AreEqual(2, Metrics.DbStats["TestDbLevel1FilesCompacted"]);
            Assert.AreEqual(3, Metrics.DbStats["TestDbLevel2Files"]);
            Assert.AreEqual(1, Metrics.DbStats["TestDbLevel2FilesCompacted"]);

            logger.Received().Warn(Arg.Is<string>(s => s.StartsWith("Cannot find 'Interval compaction' stats for Test database")));
        }

        [Test]
        public void ProcessCompactionStats_EmptyDump()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = string.Empty;
            new DbMetricsUpdater("Test", null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.AreEqual(0, Metrics.DbStats.Count);

            logger.Received().Warn("No RocksDB compaction stats available for Test databse.");
        }

        [Test]
        public void ProcessCompactionStats_NullDump()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = null;
            new DbMetricsUpdater("Test", null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.AreEqual(0, Metrics.DbStats.Count);

            logger.Received().Warn("No RocksDB compaction stats available for Test databse.");
        }
    }
}
