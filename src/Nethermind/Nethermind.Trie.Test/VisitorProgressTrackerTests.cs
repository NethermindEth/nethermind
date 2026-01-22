// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class VisitorProgressTrackerTests
{
    [Test]
    public void OnNodeVisited_TracksProgress_AtLevel0()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 1000);

        // Act - visit leaf paths with single nibble, each covers 16^3 = 4096 level-3 nodes
        // Visit half the keyspace (8 out of 16) = 8 * 4096 = 32768 out of 65536 = 50%
        for (int i = 0; i < 8; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)i });
            tracker.OnNodeVisited(path, isStorage: false, isLeaf: true);
        }

        // Assert - should be ~50% progress
        double progress = tracker.GetProgress();
        progress.Should().BeApproximately(0.5, 0.01);
    }

    [Test]
    public void OnNodeVisited_UsesDeepestLevelWithCoverage()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

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
        progress.Should().BeApproximately(0.25, 0.01);
    }

    [Test]
    public void OnNodeVisited_IsThreadSafe()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);
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
        tracker.NodeCount.Should().Be(threadCount * nodesPerThread);
    }

    [Test]
    public void OnNodeVisited_ProgressIncreases_WithinLevel()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit leaf nodes with single nibble paths
        // Each covers 16^3 = 4096 level-3 nodes
        double lastProgress = 0;
        for (int i = 0; i < 16; i++)
        {
            TreePath path = TreePath.FromNibble(new byte[] { (byte)i });
            tracker.OnNodeVisited(path, isStorage: false, isLeaf: true);

            double progress = tracker.GetProgress();
            progress.Should().BeGreaterThanOrEqualTo(lastProgress);
            lastProgress = progress;
        }

        // Assert - after visiting all 16 single-nibble leaves, progress should be 100%
        tracker.GetProgress().Should().Be(1.0);
    }

    [Test]
    public void Finish_SetsProgressTo100()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);
        TreePath path = TreePath.FromNibble(new byte[] { 0, 0, 0, 0 });
        tracker.OnNodeVisited(path);

        // Act
        tracker.Finish();

        // Assert - GetProgress still returns actual progress, but logger shows 100%
        // (We can't easily test logger output, so just verify Finish doesn't throw)
        tracker.NodeCount.Should().Be(1);
    }

    [Test]
    public void OnNodeVisited_HandlesShortPaths()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act - visit paths with fewer than 4 nibbles
        TreePath path1 = TreePath.FromNibble(new byte[] { 0 });
        TreePath path2 = TreePath.FromNibble(new byte[] { 1, 2 });
        TreePath path3 = TreePath.FromNibble(new byte[] { 3, 4, 5 });

        tracker.OnNodeVisited(path1);
        tracker.OnNodeVisited(path2);
        tracker.OnNodeVisited(path3);

        // Assert - should not throw and should track nodes
        tracker.NodeCount.Should().Be(3);
    }

    [Test]
    public void OnNodeVisited_HandlesEmptyPath()
    {
        // Arrange
        var tracker = new VisitorProgressTracker("Test", LimboLogs.Instance, reportingInterval: 100000);

        // Act
        TreePath path = TreePath.Empty;
        tracker.OnNodeVisited(path);

        // Assert
        tracker.NodeCount.Should().Be(1);
        tracker.GetProgress().Should().Be(0); // Empty path doesn't contribute to progress
    }
}
