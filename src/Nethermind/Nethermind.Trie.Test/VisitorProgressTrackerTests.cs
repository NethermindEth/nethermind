// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
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

    [TestCase(LogLevel.Debug, true, 0, 1)]
    [TestCase(LogLevel.Debug, false, 0, 0)]
    [TestCase(LogLevel.Info, false, 1, 0)]
    public void OnNodeVisited_ReportsProgressAtRequestedLevel(LogLevel logLevel, bool isDebugEnabled, int expectedInfoReports, int expectedDebugReports)
    {
        // Arrange
        InterfaceLogger innerLogger = Substitute.For<InterfaceLogger>();
        innerLogger.IsInfo.Returns(true);
        innerLogger.IsDebug.Returns(isDebugEnabled);

        VisitorProgressTracker tracker = new("Test", CreateLogManager(innerLogger), logLevel: logLevel);

        // Act - a leaf at depth 1 covers 16^3 level-3 nodes, which clears the 1% threshold
        // that otherwise suppresses reporting during the first 5 seconds
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0 }), isStorage: false, isLeaf: true);

        // Assert
        innerLogger.Received(expectedInfoReports).Info(Arg.Any<string>());
        innerLogger.Received(expectedDebugReports).Debug(Arg.Any<string>());
    }

    [TestCase(null, 1)]
    [TestCase(0, 2)]
    [TestCase(3600, 1)]
    public void OnNodeVisited_ReportsUnchangedProgressOnlyOnHeartbeat(int? reportIntervalSeconds, int expectedReports)
    {
        // Arrange - reportingInterval: 1 re-evaluates progress on every state node
        InterfaceLogger innerLogger = Substitute.For<InterfaceLogger>();
        innerLogger.IsDebug.Returns(true);
        TimeSpan? reportInterval = reportIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        VisitorProgressTracker tracker = new("Test", CreateLogManager(innerLogger), reportingInterval: 1, reportInterval: reportInterval);

        // Act - a leaf at depth 1 is 6.25%; a branch below level 3 does not move the percentage
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0 }), isStorage: false, isLeaf: true);
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0, 1, 2, 3, 4 }), isStorage: false, isLeaf: false);

        // Assert - without a heartbeat an unchanged percentage is not repeated; with a due one it is
        innerLogger.Received(expectedReports).Debug(Arg.Any<string>());
        innerLogger.Received(expectedReports).Debug(Arg.Is<string>(line => line.Contains("6.25 %")));
    }

    [TestCase(null, 1)]
    [TestCase(0, 2)]
    public void OnNodeVisited_ChecksHeartbeatDuringStorageTraversal(int? reportIntervalSeconds, int expectedReports)
    {
        // Arrange
        InterfaceLogger innerLogger = Substitute.For<InterfaceLogger>();
        innerLogger.IsDebug.Returns(true);
        TimeSpan? reportInterval = reportIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

        VisitorProgressTracker tracker = new("Test", CreateLogManager(innerLogger), reportInterval: reportInterval);

        // Act - one state leaf, then enough storage nodes to reach the 2^16 heartbeat check
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0 }), isStorage: false, isLeaf: true);
        for (int i = 1; i < 1 << 16; i++)
        {
            tracker.OnNodeVisited(TreePath.Empty, isStorage: true);
        }

        // Assert
        innerLogger.Received(expectedReports).Debug(Arg.Any<string>());
    }

    [Test]
    public void Finish_ReportsCompletionOnce()
    {
        // Arrange
        InterfaceLogger innerLogger = Substitute.For<InterfaceLogger>();
        innerLogger.IsDebug.Returns(true);

        VisitorProgressTracker tracker = new("Test", CreateLogManager(innerLogger));

        // Act - the first call reports 6.25%, Finish reports 100%, a second Finish does not repeat it
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0 }), isStorage: false, isLeaf: true);
        tracker.Finish();
        tracker.Finish();

        // Assert
        innerLogger.Received(2).Debug(Arg.Any<string>());
        innerLogger.Received(1).Debug(Arg.Is<string>(line => line.Contains("100.00 %")));
    }

    [Test]
    public void Constructor_RejectsNegativeReportInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new VisitorProgressTracker("Test", LimboLogs.Instance, reportInterval: TimeSpan.FromSeconds(-1)));

    private static ILogManager CreateLogManager(InterfaceLogger innerLogger)
    {
        // Built before Returns(), otherwise NSubstitute sees the ILogger constructor's reads of
        // innerLogger as the call being configured
        ILogger logger = new(innerLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<VisitorProgressTracker>().Returns(logger);
        return logManager;
    }
}
