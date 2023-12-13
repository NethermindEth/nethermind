// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            new DbMetricsUpdater("Test", null, null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(11));
            Assert.That(Metrics.DbStats["TestDbLevel0Files"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbLevel0FilesCompacted"], Is.EqualTo(0));
            Assert.That(Metrics.DbStats["TestDbLevel1Files"], Is.EqualTo(4));
            Assert.That(Metrics.DbStats["TestDbLevel1FilesCompacted"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbLevel2Files"], Is.EqualTo(3));
            Assert.That(Metrics.DbStats["TestDbLevel2FilesCompacted"], Is.EqualTo(1));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionGBWrite"], Is.EqualTo(10));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionMBPerSecWrite"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionGBRead"], Is.EqualTo(123));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionMBPerSecRead"], Is.EqualTo(0));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionSeconds"], Is.EqualTo(111));
        }

        [Test]
        public void ProcessCompactionStats_MissingLevels()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingLevels.txt");
            new DbMetricsUpdater("Test", null, null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(5));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionGBWrite"], Is.EqualTo(10));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionMBPerSecWrite"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionGBRead"], Is.EqualTo(123));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionMBPerSecRead"], Is.EqualTo(0));
            Assert.That(Metrics.DbStats["TestDbIntervalCompactionSeconds"], Is.EqualTo(111));
        }

        [Test]
        public void ProcessCompactionStats_MissingIntervalCompaction_Warning()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingIntervalCompaction.txt");
            new DbMetricsUpdater("Test", null, null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(6));
            Assert.That(Metrics.DbStats["TestDbLevel0Files"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbLevel0FilesCompacted"], Is.EqualTo(0));
            Assert.That(Metrics.DbStats["TestDbLevel1Files"], Is.EqualTo(4));
            Assert.That(Metrics.DbStats["TestDbLevel1FilesCompacted"], Is.EqualTo(2));
            Assert.That(Metrics.DbStats["TestDbLevel2Files"], Is.EqualTo(3));
            Assert.That(Metrics.DbStats["TestDbLevel2FilesCompacted"], Is.EqualTo(1));

            logger.Received().Warn(Arg.Is<string>(s => s.StartsWith("Cannot find 'Interval compaction' stats for Test database")));
        }

        [Test]
        public void ProcessCompactionStats_EmptyDump()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = string.Empty;
            new DbMetricsUpdater("Test", null, null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(0));

            logger.Received().Warn("No RocksDB compaction stats available for Test databse.");
        }

        [Test]
        public void ProcessCompactionStats_NullDump()
        {
            ILogger logger = Substitute.For<ILogger>();

            string testDump = null;
            new DbMetricsUpdater("Test", null, null, null, null, logger).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(0));

            logger.Received().Warn("No RocksDB compaction stats available for Test databse.");
        }
    }
}
