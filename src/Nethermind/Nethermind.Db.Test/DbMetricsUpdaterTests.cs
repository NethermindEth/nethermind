// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
