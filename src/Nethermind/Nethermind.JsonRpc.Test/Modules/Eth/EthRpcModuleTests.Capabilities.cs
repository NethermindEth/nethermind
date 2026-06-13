// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Synchronization;
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
        long? lowestInsertedBody = null,
        long? lowestInsertedReceipt = null,
        IHistoryConfig? historyConfig = null,
        IHistoryPruner? historyPruner = null)
    {
        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        IReadOnlyBlockTree blockTree = Substitute.For<IReadOnlyBlockTree>();
        blockTree.Head.Returns(head);
        blockTree.BestSuggestedHeader.Returns(head.Header);

        IStateBoundary boundary = Substitute.For<IStateBoundary>();
        boundary.OldestStateBlock.Returns(oldestStateBlock);
        boundary.RetentionWindowBlocks.Returns(retentionWindow);

        ISyncPointers syncPointers = Substitute.For<ISyncPointers>();
        syncPointers.LowestInsertedBodyNumber.Returns(lowestInsertedBody);
        syncPointers.LowestInsertedReceiptBlockNumber.Returns(lowestInsertedReceipt);

        return new EthCapabilitiesProvider(
            blockTree,
            boundary,
            syncConfig ?? new SyncConfig(),
            syncPointers,
            historyConfig ?? Substitute.For<IHistoryConfig>(),
            historyPruner ?? Substitute.For<IHistoryPruner>()).GetCapabilities();
    }

    private static IHistoryPruner MockHistoryPruner(long oldestBlockNumber)
    {
        IHistoryPruner pruner = Substitute.For<IHistoryPruner>();
        pruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(oldestBlockNumber).TestObject);
        pruner.GetRetentionBlocks(Arg.Any<long>()).Returns(call => (long)call[0] * 32);
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
        public long? LowestInsertedBody { get; init; }
        public long? LowestInsertedReceipt { get; init; }
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

        // Fast-synced node (state finalised) with default barriers (= 0): receipts present from genesis.
        yield return new CapabilitiesScenario(
            Name: "fast_sync_default_barriers_reports_genesis_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        {
            HeadNumber = 18_001_000,
            OldestStateBlock = 0,
            LowestInsertedBody = 0,
            LowestInsertedReceipt = 0,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true },
        };

        const long bodiesBarrier = 5_000_000;
        ResourceAvailability barrierBound = new(Disabled: false, OldestBlock: bodiesBarrier);
        yield return new CapabilitiesScenario(
            Name: "fast_sync_with_ancient_bodies_barrier_caps_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: barrierBound, ExpectedBlocks: barrierBound)
        {
            HeadNumber = 18_001_000,
            OldestStateBlock = 0,
            LowestInsertedBody = bodiesBarrier,
            LowestInsertedReceipt = bodiesBarrier,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true, AncientBodiesBarrier = bodiesBarrier },
        };

        yield return new CapabilitiesScenario(
            Name: "fast_sync_bodies_disabled_disables_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Disabled, ExpectedBlocks: Disabled)
        { HeadNumber = 18_001_000, OldestStateBlock = 0, SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadBodiesInFastSync = false } };

        const long bodiesBarrierLow = 3_000_000;
        const long receiptsBarrierHigh = 6_000_000;
        ResourceAvailability blocksAtBodiesBarrier = new(Disabled: false, OldestBlock: bodiesBarrierLow);
        ResourceAvailability receiptsAtReceiptsBarrier = new(Disabled: false, OldestBlock: receiptsBarrierHigh);
        yield return new CapabilitiesScenario(
            Name: "fast_sync_receipts_barrier_above_bodies_barrier",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: receiptsAtReceiptsBarrier, ExpectedBlocks: blocksAtBodiesBarrier)
        {
            HeadNumber = 18_001_000,
            OldestStateBlock = 0,
            LowestInsertedBody = bodiesBarrierLow,
            LowestInsertedReceipt = receiptsBarrierHigh,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true, AncientBodiesBarrier = bodiesBarrierLow, AncientReceiptsBarrier = receiptsBarrierHigh },
        };

        yield return new CapabilitiesScenario(
            Name: "full_sync_with_irrelevant_bodies_flag_keeps_blocks_and_receipts",
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = new SyncConfig { DownloadBodiesInFastSync = false } };

        // Mid-sync: bodies/receipts caught up to block 12M from pivot 18M; blocks/receipts oldest
        // tracks the actual progress, not the eventual barrier. State sync is finished
        // (OldestStateBlock = pivot) but historical block sync continues.
        const long midSyncBody = 12_000_000;
        const long midSyncReceipt = 12_500_000;
        const long midSyncStateFloor = 18_000_000;
        ResourceAvailability midSyncBlocks = new(Disabled: false, OldestBlock: midSyncBody);
        ResourceAvailability midSyncReceipts = new(Disabled: false, OldestBlock: midSyncReceipt);
        ResourceAvailability midSyncState = new(Disabled: false, OldestBlock: midSyncStateFloor);
        yield return new CapabilitiesScenario(
            Name: "fast_sync_mid_progress_reports_actual_lowest_inserted",
            ExpectedState: midSyncState, ExpectedStateproofs: midSyncState,
            ExpectedReceipts: midSyncReceipts, ExpectedBlocks: midSyncBlocks)
        {
            HeadNumber = 18_001_000,
            OldestStateBlock = midSyncStateFloor,
            LowestInsertedBody = midSyncBody,
            LowestInsertedReceipt = midSyncReceipt,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true },
        };

        // Pre-state-sync: fast-sync configured but OldestStateBlock not yet written → State disabled.
        yield return new CapabilitiesScenario(
            Name: "fast_sync_before_state_finalised_disables_state",
            ExpectedState: Disabled, ExpectedStateproofs: Disabled,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        {
            HeadNumber = 18_001_000,
            LowestInsertedBody = 0,
            LowestInsertedReceipt = 0,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true },
        };

        // Early fast sync: barriers configured but descending pointers null — must be Disabled.
        yield return new CapabilitiesScenario(
            Name: "fast_sync_before_first_batch_disables_blocks_and_receipts",
            ExpectedState: Disabled, ExpectedStateproofs: Disabled,
            ExpectedReceipts: Disabled, ExpectedBlocks: Disabled)
        {
            HeadNumber = 18_001_000,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = true, AncientBodiesBarrier = 5_000_000 },
        };

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
        {
            HeadNumber = 18_001_000,
            OldestStateBlock = 0,
            LowestInsertedBody = 0,
            SyncConfig = new SyncConfig { FastSync = true, PivotNumber = 18_000_000, DownloadReceiptsInFastSync = false },
        };

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
    public void Eth_capabilities_no_head_disables_all_resources()
    {
        IReadOnlyBlockTree blockTree = Substitute.For<IReadOnlyBlockTree>();
        blockTree.Head.Returns((Block?)null);

        EthCapabilities caps = new EthCapabilitiesProvider(
            blockTree,
            Substitute.For<IStateBoundary>(),
            new SyncConfig(),
            Substitute.For<ISyncPointers>(),
            Substitute.For<IHistoryConfig>(),
            Substitute.For<IHistoryPruner>()).GetCapabilities();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caps.Head, Is.EqualTo(new ChainHead(0, Hash256.Zero)));
            Assert.That(caps.State, Is.EqualTo(Disabled));
            Assert.That(caps.Stateproofs, Is.EqualTo(Disabled));
            Assert.That(caps.Receipts, Is.EqualTo(Disabled));
            Assert.That(caps.Blocks, Is.EqualTo(Disabled));
            Assert.That(caps.Tx, Is.EqualTo(Disabled));
            Assert.That(caps.Logs, Is.EqualTo(Disabled));
        }
    }

    [TestCaseSource(nameof(CapabilitiesScenarios))]
    public void Eth_capabilities_returns_expected_availability_for(CapabilitiesScenario s)
    {
        EthCapabilities caps = GetCaps(s.RetentionWindow, s.HeadNumber, s.OldestStateBlock, s.SyncConfig,
            s.LowestInsertedBody, s.LowestInsertedReceipt, s.HistoryConfig, s.HistoryPruner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caps.Head.Number, Is.EqualTo(s.HeadNumber));
            Assert.That(caps.Head.Hash, Is.Not.EqualTo(Hash256.Zero));
            Assert.That(caps.State, Is.EqualTo(s.ExpectedState), nameof(caps.State));
            Assert.That(caps.Stateproofs, Is.EqualTo(s.ExpectedStateproofs), nameof(caps.Stateproofs));
            Assert.That(caps.Tx, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Tx));
            Assert.That(caps.Logs, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Logs));
            Assert.That(caps.Receipts, Is.EqualTo(s.ExpectedReceipts), nameof(caps.Receipts));
            Assert.That(caps.Blocks, Is.EqualTo(s.ExpectedBlocks), nameof(caps.Blocks));
        }
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
    public async Task Eth_capabilities_json_matches_spec_schema()
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
