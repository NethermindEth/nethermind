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

#nullable enable

namespace Nethermind.Consensus.Test.Processing;

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

    private ProcessingStats CreateStats(long thresholdMs = 0) =>
        new(_stateReader, new ILogger(_logger), new ILogger(_slowBlockLogger), slowBlockThresholdMs: thresholdMs);

    private SlowBlockLogEntry? EmitAndParse(ProcessingStats stats, Block block, long processingMicros)
    {
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(block, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: processingMicros);

        // Report is queued to ThreadPool — poll until it arrives (up to 5s)
        int waited = 0;
        while (!_slowBlockLogger.LogList.Any() && waited < 5000)
        {
            System.Threading.Thread.Sleep(50);
            waited += 50;
        }

        if (!_slowBlockLogger.LogList.Any()) return null;
        return JsonSerializer.Deserialize<SlowBlockLogEntry>(_slowBlockLogger.LogList.Last());
    }

    [Test]
    public void Slow_block_produces_valid_JSON_with_all_sections()
    {
        ProcessingStats stats = CreateStats();
        Block block = Build.A.Block.WithNumber(12345).WithGasUsed(21_000_000)
            .WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;

        SlowBlockLogEntry? entry = EmitAndParse(stats, block, 1_500_000);

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Level, Is.EqualTo("warn"));
        Assert.That(entry.Msg, Is.EqualTo("Slow block"));
        Assert.That(entry.Block.Number, Is.EqualTo(12345));
        Assert.That(entry.Block.GasUsed, Is.EqualTo(21_000_000));
        Assert.That(entry.Block.TxCount, Is.EqualTo(2));
        Assert.That(entry.Block.Hash, Is.Not.Empty);
        Assert.That(entry.Timing.TotalMs, Is.GreaterThan(0));
        Assert.That(entry.Throughput, Is.Not.Null);
        Assert.That(entry.StateReads, Is.Not.Null);
        Assert.That(entry.StateWrites, Is.Not.Null);
        Assert.That(entry.Cache, Is.Not.Null);
        Assert.That(entry.Cache.Account, Is.Not.Null);
        Assert.That(entry.Cache.Storage, Is.Not.Null);
        Assert.That(entry.Cache.Code, Is.Not.Null);
        Assert.That(entry.Evm, Is.Not.Null);
    }

    [Test]
    public void EIP7702_fields_present_in_JSON()
    {
        ProcessingStats stats = CreateStats();
        SlowBlockLogEntry? entry = EmitAndParse(stats, Build.A.Block.WithNumber(1).TestObject, 100_000);
        string json = _slowBlockLogger.LogList.Last();

        Assert.That(json, Does.Contain("eip7702_delegations_set"));
        Assert.That(json, Does.Contain("eip7702_delegations_cleared"));
    }

    [TestCase(500_000, 1000, false, TestName = "Below_threshold_no_log")]
    [TestCase(1_500_000, 1000, true, TestName = "Above_threshold_logs")]
    [TestCase(1_000, 0, true, TestName = "Zero_threshold_logs_all")]
    public void Threshold_controls_logging(long processingMicros, long thresholdMs, bool expectLog)
    {
        ProcessingStats stats = CreateStats(thresholdMs);
        EmitAndParse(stats, Build.A.Block.WithNumber(1).TestObject, processingMicros);

        bool hasLog = _slowBlockLogger.LogList.Any(l => l.Contains("Slow block"));
        Assert.That(hasLog, Is.EqualTo(expectLog));
    }

    [Test]
    public void Cache_hit_rate_is_valid_percentage()
    {
        ProcessingStats stats = CreateStats();
        SlowBlockLogEntry? entry = EmitAndParse(stats, Build.A.Block.WithNumber(1).TestObject, 100_000);

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Cache.Account.HitRate, Is.InRange(0, 100));
        Assert.That(entry.Cache.Storage.HitRate, Is.InRange(0, 100));
        Assert.That(entry.Cache.Code.HitRate, Is.InRange(0, 100));
    }

    [Test]
    public void Timing_breakdown_is_consistent()
    {
        ProcessingStats stats = CreateStats();
        SlowBlockLogEntry? entry = EmitAndParse(stats, Build.A.Block.WithNumber(1).WithGasUsed(1_000_000).TestObject, 1_000_000);

        Assert.That(entry!.Timing.TotalMs, Is.EqualTo(1000).Within(1));
        Assert.That(entry.Timing.ExecutionMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.StateHashMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.CommitMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.BloomsMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.ReceiptsRootMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.StorageMerkleMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(entry.Timing.StateRootMs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Extended_block_and_evm_fields_present()
    {
        ProcessingStats stats = CreateStats();
        SlowBlockLogEntry? entry = EmitAndParse(stats, Build.A.Block.WithNumber(1).WithGasLimit(30_000_000).TestObject, 100_000);
        string json = _slowBlockLogger.LogList.Last();

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Block.GasLimit, Is.EqualTo(30_000_000));
        Assert.That(entry.Block.BlobCount, Is.GreaterThanOrEqualTo(0));
        Assert.That(json, Does.Contain("opcodes"));
        Assert.That(json, Does.Contain("empty_calls"));
        Assert.That(json, Does.Contain("self_destructs"));
        Assert.That(json, Does.Contain("contracts_analyzed"));
        Assert.That(json, Does.Contain("cached_contracts_used"));
        Assert.That(json, Does.Contain("evm_ms"));
        Assert.That(json, Does.Contain("blooms_ms"));
        Assert.That(json, Does.Contain("receipts_root_ms"));
        Assert.That(json, Does.Contain("storage_merkle_ms"));
        Assert.That(json, Does.Contain("state_root_ms"));
    }
}
