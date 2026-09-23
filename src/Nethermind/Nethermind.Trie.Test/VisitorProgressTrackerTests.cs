// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class VisitorProgressTrackerTests
{
    [Test]
    public void OnNodeVisited_TracksProgress_AtLevel0()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 1000);

        // Act - visit leaf paths with single nibble, each covers 16^3 = 4096 level-3 nodes
        // Visit half the keyspace (8 out of 16) = 8 * 4096 = 32768 out of 65536 = 50%
        for (int i = 0; i < 8; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)i });
            tracker.OnNodeVisited(path, isStorage: false, isLeaf: true);
        }

        // Assert - should be ~50% progress
        double progress = tracker.GetProgress();
        Assert.That(progress, Is.EqualTo(0.5).Within(0.01));
    }

    [Test]
    public void OnNodeVisited_UsesDeepestLevelWithCoverage()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit leaf nodes at 2-nibble depth
        // Each leaf at depth 2 covers 16^(3-2+1) = 16^2 = 256 level-3 nodes
        // Visit 64 leaves = 64 * 256 = 16384 out of 65536 = 25%
        for (int i = 0; i < 64; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)(i / 16), (byte)(i % 16) });
            tracker.OnNodeVisited(path, isStorage: false, isLeaf: true);
        }

        // Assert - should be ~25% progress
        double progress = tracker.GetProgress();
        Assert.That(progress, Is.EqualTo(0.25).Within(0.01));
    }

    [Test]
    public void OnNodeVisited_IsThreadSafe()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);
        const int threadCount = 8;
        const int nodesPerThread = 1000;

        // Act - visit nodes concurrently
        Parallel.For(0, threadCount, threadId =>
        {
            for (int i = 0; i < nodesPerThread; i++)
            {
                int nibble1 = (threadId * nodesPerThread + i) / 4096 % 16;
                int nibble2 = (threadId * nodesPerThread + i) / 256 % 16;
                int nibble3 = (threadId * nodesPerThread + i) / 16 % 16;
                int nibble4 = (threadId * nodesPerThread + i) % 16;
                TreePath path = TreePath.FromNibble(new byte[] { (byte)nibble1, (byte)nibble2, (byte)nibble3, (byte)nibble4 });
                tracker.OnNodeVisited(path);
            }
        });

        // Assert - node count should match
        Assert.That(tracker.NodeCount, Is.EqualTo(threadCount * nodesPerThread));
    }

    [Test]
    public void OnNodeVisited_ProgressIncreases_WithinLevel()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit leaf nodes with single nibble paths
        // Each covers 16^3 = 4096 level-3 nodes
        double lastProgress = 0;
        for (int i = 0; i < 16; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)i });
            tracker.OnNodeVisited(path, isStorage: false, isLeaf: true);

            double progress = tracker.GetProgress();
            Assert.That(progress, Is.GreaterThanOrEqualTo(lastProgress));
            lastProgress = progress;
        }

        // Assert - after visiting all 16 single-nibble leaves, progress should be 100%
        Assert.That(tracker.GetProgress(), Is.EqualTo(1.0));
    }

    [Test]
    public void Finish_SetsProgressTo100()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);
        TreePath path = TreePath.FromNibble(new byte[] { 0, 0, 0, 0 });
        tracker.OnNodeVisited(path);

        // Act
        tracker.Finish();

        // Assert - GetProgress still returns actual progress, but logger shows 100%
        // (We can't easily test logger output, so just verify Finish doesn't throw)
        Assert.That(tracker.NodeCount, Is.EqualTo(1));
    }

    [Test]
    public void OnNodeVisited_HandlesShortPaths()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit paths with fewer than 4 nibbles
        TreePath path1 = TreePath.FromNibble(new byte[] { 0 });
        TreePath path2 = TreePath.FromNibble(new byte[] { 1, 2 });
        TreePath path3 = TreePath.FromNibble(new byte[] { 3, 4, 5 });

        tracker.OnNodeVisited(path1);
        tracker.OnNodeVisited(path2);
        tracker.OnNodeVisited(path3);

        // Assert - should not throw and should track nodes
        Assert.That(tracker.NodeCount, Is.EqualTo(3));
    }

    [Test]
    public void OnNodeVisited_HandlesEmptyPath()
    {
        // Arrange
        VisitorProgressTracker tracker = new("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act
        TreePath path = TreePath.Empty;
        tracker.OnNodeVisited(path);

        // Assert
        Assert.That(tracker.NodeCount, Is.EqualTo(1));
        Assert.That(tracker.GetProgress(), Is.EqualTo(0)); // Empty path doesn't contribute to progress
    }

    [TestCase(LogLevel.Debug, true, true, 0, 1)]
    [TestCase(LogLevel.Debug, true, false, 0, 0)]
    [TestCase(LogLevel.Info, true, false, 1, 0)]
    [TestCase(LogLevel.Info, false, false, 0, 0)]
    public void OnNodeVisited_ReportsProgressAtRequestedLevel(LogLevel logLevel, bool isInfoEnabled, bool isDebugEnabled, int expectedInfoReports, int expectedDebugReports)
    {
        // Arrange
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(isInfoEnabled);
        logger.IsDebug.Returns(isDebugEnabled);
        VisitorProgressTracker tracker = new("Test", new OneLoggerLogManager(new ILogger(logger)), logLevel: logLevel);

        // Act
        tracker.OnNodeVisited(DepthOneLeaf(0), isStorage: false, isLeaf: true);

        // Assert
        logger.Received(expectedInfoReports).Info(Arg.Any<string>());
        logger.Received(expectedDebugReports).Debug(Arg.Any<string>());
    }

    [Test]
    public void OnNodeVisited_WithoutHeartbeat_ReportsEveryPercentageChangeOnce()
    {
        // Arrange - reportingInterval: 1 re-evaluates progress on every state node
        VisitorProgressTracker tracker = CreateTracker(out InterfaceLogger logger, new ManualTimeProvider(), reportingInterval: 1);

        // Act - a branch below level 3 does not move the percentage; of the seven level-3 nodes that follow,
        // only the last one takes the estimate from 4096 to 4103 / 65536, the first value that reads 6.26 %
        tracker.OnNodeVisited(DepthOneLeaf(0), isStorage: false, isLeaf: true);
        tracker.OnNodeVisited(DeepBranch, isStorage: false, isLeaf: false);
        for (int i = 0; i < 7; i++)
        {
            tracker.OnNodeVisited(LevelThreeNode(0x1000 + i));
        }

        // Assert
        logger.Received(2).Debug(Arg.Any<string>());
        Received.InOrder(() =>
        {
            logger.Debug(Arg.Is<string>(line => line.Contains("6.25 %")));
            logger.Debug(Arg.Is<string>(line => line.Contains("6.26 %")));
        });
    }

    [Test]
    public void OnNodeVisited_WithHeartbeat_ReportsOncePerIntervalWithTheCurrentNodeCount()
    {
        // Arrange
        ManualTimeProvider time = new();
        VisitorProgressTracker tracker = CreateTracker(out InterfaceLogger logger, time, TimeSpan.FromSeconds(60), reportingInterval: 1);

        // Act
        tracker.OnNodeVisited(DepthOneLeaf(0), isStorage: false, isLeaf: true); // first line straight away
        tracker.OnNodeVisited(DepthOneLeaf(1), isStorage: false, isLeaf: true); // 12.50 %, but not due yet
        time.Advance(TimeSpan.FromSeconds(59));
        tracker.OnNodeVisited(DeepBranch, isStorage: false, isLeaf: false);
        time.Advance(TimeSpan.FromSeconds(1));
        tracker.OnNodeVisited(DeepBranch, isStorage: false, isLeaf: false);
        time.Advance(TimeSpan.FromSeconds(60));
        tracker.OnNodeVisited(DeepBranch, isStorage: false, isLeaf: false); // due again without any progress

        // Assert
        logger.Received(3).Debug(Arg.Any<string>());
        Received.InOrder(() =>
        {
            logger.Debug(Arg.Is<string>(line => IsProgressLine(line, "6.25 %", 1)));
            logger.Debug(Arg.Is<string>(line => IsProgressLine(line, "12.50 %", 4)));
            logger.Debug(Arg.Is<string>(line => IsProgressLine(line, "12.50 %", 5)));
        });
    }

    [TestCase(null, 1)]
    [TestCase(60, 2)]
    public void OnNodeVisited_ChecksHeartbeatDuringStorageTraversal(int? heartbeatSeconds, int expectedReports)
    {
        // Arrange
        ManualTimeProvider time = new();
        VisitorProgressTracker tracker = CreateTracker(out InterfaceLogger logger, time, ToInterval(heartbeatSeconds));

        // Act - storage nodes never re-evaluate progress themselves, so only the check on every
        // 2^16-th visited node can notice that the interval has passed
        tracker.OnNodeVisited(DepthOneLeaf(0), isStorage: false, isLeaf: true);
        time.Advance(TimeSpan.FromSeconds(60));
        for (int i = 1; i < 1 << 16; i++)
        {
            tracker.OnNodeVisited(TreePath.Empty, isStorage: true);
        }

        // Assert
        logger.Received(expectedReports).Debug(Arg.Any<string>());
    }

    [Test]
    public void OnNodeVisited_ReportsZeroProgressOnceStartUpDelayHasPassed()
    {
        // Arrange
        ManualTimeProvider time = new();
        VisitorProgressTracker tracker = CreateTracker(out InterfaceLogger logger, time);

        // Act - three level-3 nodes are below 1 %, so nothing is written during the first 5 seconds
        for (int i = 0; i < 3; i++)
        {
            tracker.OnNodeVisited(LevelThreeNode(i));
        }

        logger.DidNotReceive().Debug(Arg.Any<string>());
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.OnNodeVisited(LevelThreeNode(3));

        // Assert - 4 / 65536 still reads 0.00 %, and that first line is written
        logger.Received(1).Debug(Arg.Any<string>());
        logger.Received(1).Debug(Arg.Is<string>(line => line.Contains(" 0.00 %")));
    }

    [TestCase(null, 1, 2)]
    [TestCase(3600, 1, 2)]
    [TestCase(null, 16, 16)]
    [TestCase(0, 16, 16)]
    public void Finish_ReportsCompletionOnce(int? heartbeatSeconds, int depthOneLeaves, int expectedReports)
    {
        // Arrange
        VisitorProgressTracker tracker = CreateTracker(out InterfaceLogger logger, new ManualTimeProvider(), ToInterval(heartbeatSeconds));

        // Act - 16 depth-1 leaves cover the whole key space, so the traversal itself already reports 100 %
        for (int i = 0; i < depthOneLeaves; i++)
        {
            tracker.OnNodeVisited(DepthOneLeaf((byte)i), isStorage: false, isLeaf: true);
        }

        tracker.Finish();
        tracker.Finish();

        // Assert
        logger.Received(expectedReports).Debug(Arg.Any<string>());
        logger.Received(1).Debug(Arg.Is<string>(line => line.Contains("100.00 %")));
    }

    [TestCase(null)]
    [TestCase(0)]
    public void OnNodeVisited_ConcurrentVisitorsNeverReportLowerProgress(int? heartbeatSeconds)
    {
        // Arrange - lines are written under the tracker's lock, so the list is in write order
        TestLogger testLogger = new();
        VisitorProgressTracker tracker = new("Test", new OneLoggerLogManager(new ILogger(testLogger)),
            heartbeatInterval: ToInterval(heartbeatSeconds), timeProvider: new ManualTimeProvider());

        // Act
        Parallel.For(0, 1 << 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => tracker.OnNodeVisited(LevelThreeNode(i)));

        // Assert - without a heartbeat each percentage is written once; with one it may repeat but never go back
        double[] percentages = testLogger.LogList.Select(ParsePercentage).ToArray();
        Assert.That(percentages, heartbeatSeconds is null ? Is.Ordered.Ascending.And.Unique : Is.Ordered.Ascending);
        Assert.That(percentages[^1], Is.EqualTo(100));
    }

    [Test]
    public void Constructor_RejectsNegativeHeartbeatInterval() =>
        Assert.That(() => new VisitorProgressTracker("Test", LimboLogs.Instance, heartbeatInterval: TimeSpan.FromSeconds(-1)),
            Throws.InstanceOf<ArgumentOutOfRangeException>().With.Property(nameof(ArgumentException.ParamName)).EqualTo("heartbeatInterval"));

    // A branch below level 3, which never moves the estimate
    private static readonly TreePath DeepBranch = TreePath.FromNibble(new byte[] { 0, 1, 2, 3, 4 });

    // Covers 16^3 of the 65536 level-3 prefixes, i.e. 6.25 %
    private static TreePath DepthOneLeaf(byte nibble) => TreePath.FromNibble(new[] { nibble });

    private static TreePath LevelThreeNode(int index) =>
        TreePath.FromNibble(new[] { (byte)(index >> 12 & 0xF), (byte)(index >> 8 & 0xF), (byte)(index >> 4 & 0xF), (byte)(index & 0xF) });

    private static TimeSpan? ToInterval(int? seconds) => seconds is { } s ? TimeSpan.FromSeconds(s) : null;

    private static VisitorProgressTracker CreateTracker(out InterfaceLogger logger, TimeProvider timeProvider, TimeSpan? heartbeatInterval = null, int reportingInterval = 100_000)
    {
        logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        return new VisitorProgressTracker("Test", new OneLoggerLogManager(new ILogger(logger)), reportingInterval,
            heartbeatInterval: heartbeatInterval, timeProvider: timeProvider);
    }

    private static bool IsProgressLine(string line, string percentage, int nodes) =>
        line.Contains(percentage) && Regex.IsMatch(line, $@"nodes:\s+{nodes}$");

    private static double ParsePercentage(string line) =>
        double.Parse(Regex.Match(line, @"(\d+\.\d{2}) %").Groups[1].Value, CultureInfo.InvariantCulture);
}
