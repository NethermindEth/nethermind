// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db.Rocks.Statistics;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using RocksDbSharp;

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
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_AllData.txt");
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(11));
            Assert.That(Metrics.DbStats[("TestDb", "Level0Files")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "Level0FilesCompacted")], Is.EqualTo(0));
            Assert.That(Metrics.DbStats[("TestDb", "Level1Files")], Is.EqualTo(4));
            Assert.That(Metrics.DbStats[("TestDb", "Level1FilesCompacted")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "Level2Files")], Is.EqualTo(3));
            Assert.That(Metrics.DbStats[("TestDb", "Level2FilesCompacted")], Is.EqualTo(1));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionGBWrite")], Is.EqualTo(10));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionMBPerSecWrite")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionGBRead")], Is.EqualTo(123));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionMBPerSecRead")], Is.EqualTo(0));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionSeconds")], Is.EqualTo(111));
        }

        [Test]
        public void ProcessStats_AllDataExist()
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();

            string testDump = File.ReadAllText(@"InputFiles/SampleStats.txt");
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessStatisticsString(testDump);

            Assert.That(Metrics.DbStats[("TestDb", "rocksdb.prefetch.bytes")], Is.EqualTo(1));
            Assert.That(Metrics.DbStats[("TestDb", "rocksdb.prefetch.bytes.useful")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "rocksdb.prefetch.hits")], Is.EqualTo(3));
            Assert.That(Metrics.DbStats[("TestDb", "rocksdb.db.get.micros.count")], Is.EqualTo(4));
            Assert.That(Metrics.DbStats[("TestDb", "rocksdb.db.get.micros.sum")], Is.EqualTo(5));
        }

        [Test]
        public void ProcessCompactionStats_MissingLevels()
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingLevels.txt");
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(5));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionGBWrite")], Is.EqualTo(10));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionMBPerSecWrite")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionGBRead")], Is.EqualTo(123));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionMBPerSecRead")], Is.EqualTo(0));
            Assert.That(Metrics.DbStats[("TestDb", "IntervalCompactionSeconds")], Is.EqualTo(111));
        }

        [Test]
        public void ProcessCompactionStats_MissingIntervalCompaction_Warning()
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsWarn.Returns(true);

            string testDump = File.ReadAllText(@"InputFiles/CompactionStatsExample_MissingIntervalCompaction.txt");
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(6));
            Assert.That(Metrics.DbStats[("TestDb", "Level0Files")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "Level0FilesCompacted")], Is.EqualTo(0));
            Assert.That(Metrics.DbStats[("TestDb", "Level1Files")], Is.EqualTo(4));
            Assert.That(Metrics.DbStats[("TestDb", "Level1FilesCompacted")], Is.EqualTo(2));
            Assert.That(Metrics.DbStats[("TestDb", "Level2Files")], Is.EqualTo(3));
            Assert.That(Metrics.DbStats[("TestDb", "Level2FilesCompacted")], Is.EqualTo(1));

            logger.Received().Warn(Arg.Is<string>(s => s.StartsWith("Cannot find 'Interval compaction' stats for Test database")));
        }

        [Test]
        public void ProcessCompactionStats_EmptyDump()
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsWarn.Returns(true);

            string testDump = string.Empty;
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(0));

            logger.Received().Warn("No RocksDB compaction stats available for Test database.");
        }

        [Test]
        public void ProcessCompactionStats_NullDump()
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsWarn.Returns(true);

            string testDump = null;
            new DbMetricsUpdater<DbOptions>("Test", null, null, null, null, new(logger)).ProcessCompactionStats(testDump);

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(0));

            logger.Received().Warn("No RocksDB compaction stats available for Test database.");
        }
    }
}
