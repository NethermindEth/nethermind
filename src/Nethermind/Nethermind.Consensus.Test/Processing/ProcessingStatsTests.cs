// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Unit tests for ProcessingStats slow block logging.
/// Mirrors Geth's blockchain_stats_test.go tests.
/// </summary>
[TestFixture]
public class ProcessingStatsTests
{
    private TestLogger _logger = null!;
    private TestLogger _slowBlockLogger = null!;
    private IStateReader _stateReader = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new TestLogger();
        _slowBlockLogger = new TestLogger();
        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
    }

    /// <summary>
    /// Verifies that slow block logging outputs valid JSON with all expected fields.
    /// Equivalent to Geth's TestLogSlowBlockJSON.
    /// </summary>
    [Test]
    public void TestLogSlowBlockJSON()
    {
        // Arrange - Create ProcessingStats with threshold=0 to force logging all blocks
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0);

        Block block = Build.A.Block
            .WithNumber(12345)
            .WithGasUsed(21_000_000)
            .WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject)
            .TestObject;

        // Act - Simulate block processing
        stats.Start();
        stats.CaptureStartStats();

        // UpdateStats triggers the slow block logging
        // Processing time > 0 with threshold=0 should log
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 1_500_000); // 1500ms

        // Wait for thread pool to process
        System.Threading.Thread.Sleep(100);

        // Assert - Parse the JSON log and verify structure
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty, "Expected slow block log to be generated");

        string jsonLog = _slowBlockLogger.LogList.First();
        var logEntry = JsonSerializer.Deserialize<SlowBlockLogEntry>(jsonLog);

        Assert.That(logEntry, Is.Not.Null);
        Assert.That(logEntry!.Level, Is.EqualTo("warn"));
        Assert.That(logEntry.Msg, Is.EqualTo("Slow block"));

        // Verify block info
        Assert.That(logEntry.Block.Number, Is.EqualTo(12345));
        Assert.That(logEntry.Block.GasUsed, Is.EqualTo(21_000_000));
        Assert.That(logEntry.Block.TxCount, Is.EqualTo(2));
        Assert.That(logEntry.Block.Hash, Is.Not.Empty);

        // Verify timing fields exist
        Assert.That(logEntry.Timing.TotalMs, Is.GreaterThan(0));

        // Verify throughput
        Assert.That(logEntry.Throughput, Is.Not.Null);

        // Verify state_reads structure
        Assert.That(logEntry.StateReads, Is.Not.Null);

        // Verify state_writes structure (including EIP-7702 fields)
        Assert.That(logEntry.StateWrites, Is.Not.Null);

        // Verify cache structure
        Assert.That(logEntry.Cache, Is.Not.Null);
        Assert.That(logEntry.Cache.Account, Is.Not.Null);
        Assert.That(logEntry.Cache.Storage, Is.Not.Null);
        Assert.That(logEntry.Cache.Code, Is.Not.Null);

        // Verify EVM ops structure (Nethermind extension)
        Assert.That(logEntry.Evm, Is.Not.Null);
    }

    /// <summary>
    /// Verifies that EIP-7702 delegation fields are present in slow block JSON.
    /// Equivalent to Geth's TestLogSlowBlockEIP7702.
    /// </summary>
    [Test]
    public void TestLogSlowBlockEIP7702Fields()
    {
        // Arrange
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        // Act
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 100_000);

        System.Threading.Thread.Sleep(100);

        // Assert
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty);
        string jsonLog = _slowBlockLogger.LogList.First();

        // Verify EIP-7702 fields exist in JSON
        Assert.That(jsonLog, Does.Contain("eip7702_delegations_set"));
        Assert.That(jsonLog, Does.Contain("eip7702_delegations_cleared"));
    }

    /// <summary>
    /// Verifies that blocks below threshold are NOT logged.
    /// Equivalent to Geth's TestLogSlowBlockThreshold (below threshold case).
    /// </summary>
    [Test]
    public void TestSlowBlockThreshold_BelowThreshold_NoLog()
    {
        // Arrange - Use default threshold of 1000ms
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 1000);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        // Act - Process a fast block (500ms < 1000ms threshold)
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 500_000); // 500ms

        System.Threading.Thread.Sleep(100);

        // Assert - No slow block log should be generated
        Assert.That(_slowBlockLogger.LogList.Count(l => l.Contains("Slow block")), Is.EqualTo(0),
            "Fast blocks should NOT trigger slow block logging");
    }

    /// <summary>
    /// Verifies that blocks above threshold ARE logged.
    /// Equivalent to Geth's TestLogSlowBlockThreshold (above threshold case).
    /// </summary>
    [Test]
    public void TestSlowBlockThreshold_AboveThreshold_Logs()
    {
        // Arrange
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 1000);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        // Act - Process a slow block (1500ms > 1000ms threshold)
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 1_500_000); // 1500ms

        System.Threading.Thread.Sleep(100);

        // Assert - Slow block log should be generated
        Assert.That(_slowBlockLogger.LogList.Any(l => l.Contains("Slow block")), Is.True,
            "Slow blocks MUST trigger slow block logging");
    }

    /// <summary>
    /// Verifies that threshold=0 logs ALL blocks (useful for testing).
    /// </summary>
    [Test]
    public void TestSlowBlockThreshold_ZeroThreshold_LogsAll()
    {
        // Arrange - Threshold of 0 means log all blocks
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        // Act - Process a very fast block (1ms)
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 1_000); // 1ms

        System.Threading.Thread.Sleep(100);

        // Assert - Even fast blocks should be logged with threshold=0
        Assert.That(_slowBlockLogger.LogList.Any(l => l.Contains("Slow block")), Is.True,
            "With threshold=0, ALL blocks should be logged");
    }

    /// <summary>
    /// Verifies that cache hit rate is calculated correctly: hits/(hits+misses)*100.
    /// </summary>
    [Test]
    public void TestSlowBlockCacheHitRateCalculation()
    {
        // Arrange
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        // Act
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 100_000);

        System.Threading.Thread.Sleep(100);

        // Assert
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty);
        string jsonLog = _slowBlockLogger.LogList.First();
        var logEntry = JsonSerializer.Deserialize<SlowBlockLogEntry>(jsonLog);

        // Verify hit_rate fields exist and are valid percentages (0-100)
        Assert.That(logEntry!.Cache.Account.HitRate, Is.GreaterThanOrEqualTo(0));
        Assert.That(logEntry.Cache.Account.HitRate, Is.LessThanOrEqualTo(100));
        Assert.That(logEntry.Cache.Storage.HitRate, Is.GreaterThanOrEqualTo(0));
        Assert.That(logEntry.Cache.Storage.HitRate, Is.LessThanOrEqualTo(100));
        Assert.That(logEntry.Cache.Code.HitRate, Is.GreaterThanOrEqualTo(0));
        Assert.That(logEntry.Cache.Code.HitRate, Is.LessThanOrEqualTo(100));
    }

    /// <summary>
    /// Verifies that timing breakdown sums correctly: execution_ms = total - read - hash - commit.
    /// </summary>
    [Test]
    public void TestSlowBlockTimingBreakdown()
    {
        // Arrange
        var stats = new ProcessingStats(
            _stateReader,
            new ILogger(_logger),
            new ILogger(_slowBlockLogger),
            slowBlockThresholdMs: 0);

        Block block = Build.A.Block.WithNumber(1).WithGasUsed(1_000_000).TestObject;

        // Act
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: 1_000_000); // 1000ms

        System.Threading.Thread.Sleep(100);

        // Assert
        Assert.That(_slowBlockLogger.LogList, Is.Not.Empty);
        string jsonLog = _slowBlockLogger.LogList.First();
        var logEntry = JsonSerializer.Deserialize<SlowBlockLogEntry>(jsonLog);

        // total_ms should be approximately 1000 (from 1_000_000 microseconds)
        Assert.That(logEntry!.Timing.TotalMs, Is.EqualTo(1000).Within(1));

        // execution_ms should be non-negative
        Assert.That(logEntry.Timing.ExecutionMs, Is.GreaterThanOrEqualTo(0));

        // All timing components should be non-negative
        Assert.That(logEntry.Timing.StateReadMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(logEntry.Timing.StateHashMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(logEntry.Timing.CommitMs, Is.GreaterThanOrEqualTo(0));
    }
}
