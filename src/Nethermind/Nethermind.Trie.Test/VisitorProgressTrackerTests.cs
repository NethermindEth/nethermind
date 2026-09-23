// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
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

    [TestCase(0.01, 2)]
    [TestCase(1, 1)]
    [TestCase(10, 1)]
    public void OnNodeVisited_ReportsOncePerRequestedProgressStep(double reportEveryPercent, int expectedReports)
    {
        // Arrange
        InterfaceLogger innerLogger = Substitute.For<InterfaceLogger>();
        innerLogger.IsDebug.Returns(true);

        VisitorProgressTracker tracker = new("Test", CreateLogManager(innerLogger), reportEveryPercent: reportEveryPercent);

        // Act - a leaf at depth 1 is 6.25%, a leaf at depth 2 adds 0.39%: the second one
        // crosses a 0.01% step but not a 1% step. The first one is reported even when it is
        // below the step (10%), so a run always shows a line as soon as reporting starts.
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 0 }), isStorage: false, isLeaf: true);
        tracker.OnNodeVisited(TreePath.FromNibble(new byte[] { 1, 0 }), isStorage: false, isLeaf: true);

        // Assert - the step throttles how often a line is written, not the precision of the line
        innerLogger.Received(expectedReports).Debug(Arg.Any<string>());
        innerLogger.Received(1).Debug(Arg.Is<string>(line => line.Contains("6.25 %")));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(100.01)]
    public void Constructor_RejectsReportStepOutsideOfPercentRange(double reportEveryPercent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new VisitorProgressTracker("Test", LimboLogs.Instance, reportEveryPercent: reportEveryPercent));

    private static ILogManager CreateLogManager(InterfaceLogger innerLogger)
    {
        // Built before Returns(), otherwise NSubstitute sees the ILogger constructor's reads of
        // innerLogger as the call being configured
        ILogger logger = new(innerLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<ProgressLogger>().Returns(logger);
        return logManager;
    }
}
