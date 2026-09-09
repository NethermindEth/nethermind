// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private WaitableTestLogger _slowBlockLogger = null!;
    private IStateReader _stateReader = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new TestLogger();
        _slowBlockLogger = new WaitableTestLogger();
        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
    }

    private ProcessingStats CreateStats(long thresholdMs = 0) =>
        new(_stateReader, new ILogger(_logger), new ILogger(_slowBlockLogger), slowBlockThresholdMs: thresholdMs);

    private SlowBlockLogEntry? EmitAndParse(ProcessingStats stats, Block block, long processingMicros)
    {
        stats.Start();
        stats.CaptureStartStats();
        stats.UpdateStats(new[] { block }, Build.A.BlockHeader.TestObject, blockProcessingTimeInMicros: processingMicros);

        // Report is queued to ThreadPool — wait deterministically on the logger's MRES.
        _slowBlockLogger.WaitForEntry(TimeSpan.FromSeconds(5));

        if (!_slowBlockLogger.LogList.Any()) return null;
        return JsonSerializer.Deserialize<SlowBlockLogEntry>(_slowBlockLogger.LogList.Last());
    }

    [Test]
    public void Slow_block_JSON_matches_schema()
    {
        ProcessingStats stats = CreateStats();
        Block block = Build.A.Block.WithNumber(12345).WithGasUsed(21_000_000).WithGasLimit(30_000_000)
            .WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.TestObject).TestObject;

        SlowBlockLogEntry? entry = EmitAndParse(stats, block, 1_500_000);
        string json = _slowBlockLogger.LogList.Last();

        AssertSlowBlockSchema(json);

        // Spot-check a few values that the schema can't express (presence + type only).
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Level, Is.EqualTo("warn"));
        Assert.That(entry.Msg, Is.EqualTo("Slow block"));
        Assert.That(entry.Block.Number, Is.EqualTo(12345));
        Assert.That(entry.Block.GasUsed, Is.EqualTo(21_000_000));
        Assert.That(entry.Block.GasLimit, Is.EqualTo(30_000_000));
        Assert.That(entry.Block.TxCount, Is.EqualTo(2));
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

    // Declarative schema for the slow-block log JSON. Each entry asserts a dotted property path
    // is present with the expected JSON value kind. Drives <see cref="AssertSlowBlockSchema"/>.
    private static readonly (string Path, JsonValueKind Kind)[] SlowBlockSchema =
    [
        ("level", JsonValueKind.String),
        ("msg", JsonValueKind.String),
        // parallel_execution emits as a JSON boolean (true or false); the schema validator below
        // accepts either via SchemaKind.Boolean which maps to True|False.
        ("parallel_execution", JsonValueKind.False),
        ("block.number", JsonValueKind.Number),
        ("block.hash", JsonValueKind.String),
        ("block.gas_used", JsonValueKind.Number),
        ("block.gas_limit", JsonValueKind.Number),
        ("block.tx_count", JsonValueKind.Number),
        ("block.blob_count", JsonValueKind.Number),
        ("timing.execution_ms", JsonValueKind.Number),
        ("timing.evm_ms", JsonValueKind.Number),
        ("timing.blooms_ms", JsonValueKind.Number),
        ("timing.receipts_root_ms", JsonValueKind.Number),
        ("timing.commit_ms", JsonValueKind.Number),
        ("timing.storage_merkle_ms", JsonValueKind.Number),
        ("timing.state_root_ms", JsonValueKind.Number),
        ("timing.state_hash_ms", JsonValueKind.Number),
        ("timing.total_ms", JsonValueKind.Number),
        ("throughput.mgas_per_sec", JsonValueKind.Number),
        ("state_reads.accounts", JsonValueKind.Number),
        ("state_reads.storage_slots", JsonValueKind.Number),
        ("state_reads.code", JsonValueKind.Number),
        ("state_reads.code_bytes", JsonValueKind.Number),
        ("state_writes.accounts", JsonValueKind.Number),
        ("state_writes.accounts_deleted", JsonValueKind.Number),
        ("state_writes.storage_slots", JsonValueKind.Number),
        ("state_writes.storage_slots_deleted", JsonValueKind.Number),
        ("state_writes.code", JsonValueKind.Number),
        ("state_writes.code_bytes", JsonValueKind.Number),
        ("state_writes.eip7702_delegations_set", JsonValueKind.Number),
        ("state_writes.eip7702_delegations_cleared", JsonValueKind.Number),
        ("cache.account.hits", JsonValueKind.Number),
        ("cache.account.misses", JsonValueKind.Number),
        ("cache.account.hit_rate", JsonValueKind.Number),
        ("cache.storage.hits", JsonValueKind.Number),
        ("cache.storage.misses", JsonValueKind.Number),
        ("cache.storage.hit_rate", JsonValueKind.Number),
        ("cache.code.hits", JsonValueKind.Number),
        ("cache.code.misses", JsonValueKind.Number),
        ("cache.code.hit_rate", JsonValueKind.Number),
        ("evm.opcodes", JsonValueKind.Number),
        ("evm.sload", JsonValueKind.Number),
        ("evm.sstore", JsonValueKind.Number),
        ("evm.calls", JsonValueKind.Number),
        ("evm.empty_calls", JsonValueKind.Number),
        ("evm.creates", JsonValueKind.Number),
        ("evm.self_destructs", JsonValueKind.Number),
        ("evm.contracts_analyzed", JsonValueKind.Number),
        ("evm.cached_contracts_used", JsonValueKind.Number),
    ];

    private static void AssertSlowBlockSchema(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), "Root must be an object");
        foreach ((string path, JsonValueKind kind) in SlowBlockSchema)
        {
            AssertJsonPath(doc.RootElement, path, kind);
        }
    }

    private static void AssertJsonPath(JsonElement root, string path, JsonValueKind expectedKind)
    {
        JsonElement current = root;
        foreach (string segment in path.Split('.'))
        {
            Assert.That(current.TryGetProperty(segment, out JsonElement next), Is.True,
                $"Missing JSON property: {path}");
            current = next;
        }
        Assert.That(current.ValueKind, Is.EqualTo(expectedKind),
            $"Wrong JSON value kind at {path}: expected {expectedKind}, got {current.ValueKind}");
    }
}
