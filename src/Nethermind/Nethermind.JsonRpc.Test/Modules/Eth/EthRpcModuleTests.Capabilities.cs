// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.History;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.State;
using NJsonSchema;
using NJsonSchema.Validation;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private static readonly ResourceAvailability Available = new(Disabled: false, OldestBlock: 0L);
    private static readonly ResourceAvailability Disabled = new(Disabled: true, OldestBlock: null);

    private static EthCapabilities GetCaps(
        long? retentionWindow = null,
        long headNumber = 1000,
        long? oldestStateBlock = null,
        SyncConfig? syncConfig = null,
        IHistoryConfig? historyConfig = null,
        IHistoryPruner? historyPruner = null)
    {
        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);
        blockTree.BestSuggestedHeader.Returns(head.Header);
        blockTree.OldestStateBlock.Returns(oldestStateBlock);

        IWorldStateManager wsm = Substitute.For<IWorldStateManager>();
        wsm.GetOldestStateBlock(Arg.Any<long>()).Returns(call =>
            retentionWindow is { } w ? Math.Max(0L, (long)call[0] - w) : (long?)null);

        return new EthCapabilitiesProvider(blockTree, wsm, syncConfig, historyConfig, historyPruner).GetCapabilities();
    }

    private static IHistoryPruner MockHistoryPruner(long oldestBlockNumber)
    {
        IHistoryPruner pruner = Substitute.For<IHistoryPruner>();
        pruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(oldestBlockNumber).TestObject);
        return pruner;
    }

    public sealed record CapabilitiesScenario(
        string Name,
        ResourceAvailability ExpectedState,
        ResourceAvailability ExpectedStateproofs,
        ResourceAvailability ExpectedReceipts,
        ResourceAvailability ExpectedBlocks)
    {
        public long? RetentionWindow { get; init; }
        public long HeadNumber { get; init; } = 1000;
        public long? OldestStateBlock { get; init; }
        public SyncConfig? SyncConfig { get; init; }
        public IHistoryConfig? HistoryConfig { get; init; }
        public IHistoryPruner? HistoryPruner { get; init; }
        public override string ToString() => Name;
    }

    private static IEnumerable<CapabilitiesScenario> CapabilitiesScenarios()
    {
        SyncConfig fullSync = new() { DownloadReceiptsInFastSync = true, PivotNumber = 0 };

        yield return new CapabilitiesScenario(
            Name: "archive_full_availability",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = fullSync };

        // Fast-synced node with default barriers (= 0): receipts are present from genesis.
        yield return new CapabilitiesScenario(
            Name: "fast_sync_default_barriers_reports_genesis_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { HeadNumber = 18_001_000, SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true } };

        // Fast-synced node with AncientBodiesBarrier > 0: bodies (and therefore blocks/receipts) are missing below the barrier.
        const long bodiesBarrier = 5_000_000;
        ResourceAvailability barrierBound = new(Disabled: false, OldestBlock: bodiesBarrier);
        yield return new CapabilitiesScenario(
            Name: "fast_sync_with_ancient_bodies_barrier_caps_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: barrierBound, ExpectedBlocks: barrierBound)
        {
            HeadNumber = 18_001_000,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true, AncientBodiesBarrier = bodiesBarrier },
        };

        yield return new CapabilitiesScenario(
            Name: "fast_sync_bodies_disabled_disables_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Disabled, ExpectedBlocks: Disabled)
        { HeadNumber = 18_001_000, SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadBodiesInFastSync = false } };

        yield return new CapabilitiesScenario(
            Name: "full_sync_with_irrelevant_bodies_flag_keeps_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = new SyncConfig { DownloadBodiesInFastSync = false } };

        const long fastSyncFloor = 18_000_000;
        ResourceAvailability fastSyncedState = new(Disabled: false, OldestBlock: fastSyncFloor);
        yield return new CapabilitiesScenario(
            Name: "archive_after_fast_sync_reports_pivot_floor",
            ExpectedState: fastSyncedState, ExpectedStateproofs: fastSyncedState,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { HeadNumber = 18_001_000, OldestStateBlock = fastSyncFloor, SyncConfig = fullSync };

        yield return new CapabilitiesScenario(
            Name: "full_pruning_before_first_run_looks_archive",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = fullSync };

        const long fullPruneFloor = 500;
        ResourceAvailability fullPruned = new(Disabled: false, OldestBlock: fullPruneFloor);
        yield return new CapabilitiesScenario(
            Name: "full_pruning_after_run_reports_copied_state_floor",
            ExpectedState: fullPruned, ExpectedStateproofs: fullPruned,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { OldestStateBlock = fullPruneFloor, SyncConfig = fullSync };

        const long retention = 128;
        const long memoryHead = 1000;
        ResourceAvailability memoryPruned = new(
            Disabled: false,
            OldestBlock: memoryHead - retention,
            DeleteStrategy: new DeleteStrategy("window", retention));
        yield return new CapabilitiesScenario(
            Name: "memory_pruning_window_dominates_old_floor",
            ExpectedState: memoryPruned, ExpectedStateproofs: memoryPruned,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { RetentionWindow = retention, HeadNumber = memoryHead, OldestStateBlock = 0, SyncConfig = fullSync };

        // Floor dominates window — DeleteStrategy is suppressed so oldestBlock and head-retentionBlocks stay consistent.
        const long recentPivot = 950;
        ResourceAvailability postSyncMemory = new(Disabled: false, OldestBlock: recentPivot);
        yield return new CapabilitiesScenario(
            Name: "memory_pruning_floor_dominates_window",
            ExpectedState: postSyncMemory, ExpectedStateproofs: postSyncMemory,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { RetentionWindow = retention, HeadNumber = memoryHead, OldestStateBlock = recentPivot, SyncConfig = fullSync };

        yield return new CapabilitiesScenario(
            Name: "fast_sync_no_receipts_disables_tx_logs_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Disabled, ExpectedBlocks: Available)
        { HeadNumber = 18_001_000, SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = false } };

        yield return new CapabilitiesScenario(
            Name: "full_sync_with_irrelevant_receipts_flag_keeps_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = new SyncConfig { DownloadReceiptsInFastSync = false } };

        const long rollingFloor = 1000;
        const uint retentionEpochs = 200;
        ResourceAvailability rollingPruned = new(
            Disabled: false,
            OldestBlock: rollingFloor,
            DeleteStrategy: new DeleteStrategy("window", retentionEpochs * 32));
        yield return new CapabilitiesScenario(
            Name: "rolling_history_pruning_window_and_floor",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: rollingPruned, ExpectedBlocks: rollingPruned)
        {
            SyncConfig = fullSync,
            HistoryConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = retentionEpochs },
            HistoryPruner = MockHistoryPruner(rollingFloor),
        };

        const long ancientFloor = 500;
        ResourceAvailability ancientPruned = new(Disabled: false, OldestBlock: ancientFloor);
        yield return new CapabilitiesScenario(
            Name: "ancient_barriers_history_pruning_advances_floor",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: ancientPruned, ExpectedBlocks: ancientPruned)
        {
            SyncConfig = fullSync,
            HistoryConfig = new HistoryConfig { Pruning = PruningModes.UseAncientBarriers },
            HistoryPruner = MockHistoryPruner(ancientFloor),
        };
    }

    [Test]
    public void eth_capabilities_no_head_disables_all_resources()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);
        IWorldStateManager wsm = Substitute.For<IWorldStateManager>();

        EthCapabilities caps = new EthCapabilitiesProvider(blockTree, wsm).GetCapabilities();

        Assert.That(caps.Head, Is.EqualTo(new ChainHead(0, Hash256.Zero)));
        Assert.That(caps.State, Is.EqualTo(Disabled));
        Assert.That(caps.Stateproofs, Is.EqualTo(Disabled));
        Assert.That(caps.Receipts, Is.EqualTo(Disabled));
        Assert.That(caps.Blocks, Is.EqualTo(Disabled));
        Assert.That(caps.Tx, Is.EqualTo(Disabled));
        Assert.That(caps.Logs, Is.EqualTo(Disabled));
        wsm.DidNotReceive().GetOldestStateBlock(Arg.Any<long>());
    }

    [TestCaseSource(nameof(CapabilitiesScenarios))]
    public void eth_capabilities_scenario(CapabilitiesScenario s)
    {
        EthCapabilities caps = GetCaps(s.RetentionWindow, s.HeadNumber, s.OldestStateBlock, s.SyncConfig, s.HistoryConfig, s.HistoryPruner);

        Assert.That(caps.Head.Number, Is.EqualTo(s.HeadNumber));
        Assert.That(caps.Head.Hash, Is.Not.EqualTo(Hash256.Zero));
        Assert.That(caps.State, Is.EqualTo(s.ExpectedState), nameof(caps.State));
        Assert.That(caps.Stateproofs, Is.EqualTo(s.ExpectedStateproofs), nameof(caps.Stateproofs));
        Assert.That(caps.Tx, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Tx));
        Assert.That(caps.Logs, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Logs));
        Assert.That(caps.Receipts, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Receipts));
        Assert.That(caps.Blocks, Is.EqualTo(s.ExpectedBlocks), nameof(caps.Blocks));
    }

    /// <summary>
    /// JSON schema mirroring ethereum/execution-apis#755 — guards against naming-policy regressions
    /// (e.g. accidental return to <c>blockNumber</c>/<c>blockHash</c>) and missing required fields.
    /// </summary>
    private const string EthCapabilitiesSchema = """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "additionalProperties": false,
          "required": ["head", "state", "tx", "logs", "receipts", "blocks", "stateproofs"],
          "properties": {
            "head": {
              "type": "object",
              "additionalProperties": false,
              "required": ["number", "hash"],
              "properties": {
                "number": { "type": "string", "pattern": "^0x[0-9a-fA-F]+$" },
                "hash": { "type": "string", "pattern": "^0x[0-9a-fA-F]{64}$" }
              }
            },
            "state": { "$ref": "#/definitions/resource" },
            "tx": { "$ref": "#/definitions/resource" },
            "logs": { "$ref": "#/definitions/resource" },
            "receipts": { "$ref": "#/definitions/resource" },
            "blocks": { "$ref": "#/definitions/resource" },
            "stateproofs": { "$ref": "#/definitions/resource" }
          },
          "definitions": {
            "resource": {
              "type": "object",
              "additionalProperties": false,
              "required": ["disabled"],
              "properties": {
                "disabled": { "type": "boolean" },
                "oldestBlock": { "type": "string", "pattern": "^0x[0-9a-fA-F]+$" },
                "deleteStrategy": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["type", "retentionBlocks"],
                  "properties": {
                    "type": { "type": "string", "enum": ["window"] },
                    "retentionBlocks": { "type": "integer", "minimum": 0 }
                  }
                }
              }
            }
          }
        }
        """;

    [Test]
    public async Task eth_capabilities_json_matches_spec_schema()
    {
        EthCapabilities caps = GetCaps(
            retentionWindow: 64,
            headNumber: 1000,
            syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 });

        string json = new EthereumJsonSerializer().Serialize(caps);
        JsonSchema schema = await JsonSchema.FromJsonAsync(EthCapabilitiesSchema);
        ICollection<ValidationError> errors = schema.Validate(json);

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }
}
