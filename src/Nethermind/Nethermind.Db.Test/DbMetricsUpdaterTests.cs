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

            Assert.That(Metrics.DbStats.Count, Is.EqualTo(5));
            // Level    Files   Size     Score Read(GB)  Rn(GB) Rnp1(GB) Write(GB) Wnew(GB) Moved(GB) W-Amp Rd(MB/s) Wr(MB/s) Comp(sec) CompMergeCPU(sec) Comp(cnt) Avg(sec) KeyIn KeyDrop Rblob(GB) Wblob(GB)
            // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
            // L0     94/0    1.22 GB   0.1      0.2     0.3      0.4      0.5     0.6       0.7   0.8      0.9     1.0    1.1            1.2      1.3    1.4       0      0       0.0       0.0

            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "files")], Is.EqualTo(94));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "files_compacting")], Is.EqualTo(0));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "score")], Is.EqualTo(0.1));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "size")], Is.EqualTo(1309965025.28));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "read")], Is.EqualTo(0.2));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "rn")], Is.EqualTo(0.3));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "rnp1")], Is.EqualTo(0.4));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "write")], Is.EqualTo(0.5));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "wnew")], Is.EqualTo(0.6));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "moved")], Is.EqualTo(0.7));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "wamp")], Is.EqualTo(0.8));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "rd")], Is.EqualTo(0.9));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "wr")], Is.EqualTo(1.0));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "comp_sec")], Is.EqualTo(1.1));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "comp_merge_cpu_sec")], Is.EqualTo(1.2));
            Assert.That(Metrics.DbCompactionStats[("TestDb", 0, "comp_total")], Is.EqualTo(1.3));
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
