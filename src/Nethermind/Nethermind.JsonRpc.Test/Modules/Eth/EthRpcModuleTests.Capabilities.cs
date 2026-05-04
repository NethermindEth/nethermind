// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private static readonly StateAvailability ArchiveState = new(RetentionWindowBlocks: null);
    private static readonly StateAvailability FullPrunedState = new(RetentionWindowBlocks: null);

    private static EthCapabilities GetCaps(
        StateAvailability state,
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
        wsm.StateAvailability.Returns(state);

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
        StateAvailability State,
        ResourceAvailability ExpectedState,
        ResourceAvailability ExpectedStateproofs,
        ResourceAvailability ExpectedReceipts,
        ResourceAvailability ExpectedBlocks)
    {
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

        // Archive sync from genesis — no floor recorded → oldest defaults to 0.
        // Also covers the genesis-receipts regression (AncientReceiptsBarrierCalc Math.Max(1, …) on PivotNumber=0).
        yield return new CapabilitiesScenario(
            Name: "archive_full_availability",
            State: ArchiveState,
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = fullSync };

        // Fast/snap synced archive node — sync recorded the pivot as the floor.
        const long fastSyncFloor = 18_000_000;
        ResourceAvailability fastSyncedState = new(Disabled: false, OldestBlock: fastSyncFloor);
        yield return new CapabilitiesScenario(
            Name: "archive_after_fast_sync_reports_pivot_floor",
            State: ArchiveState,
            ExpectedState: fastSyncedState, ExpectedStateproofs: fastSyncedState,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { HeadNumber = 18_001_000, OldestStateBlock = fastSyncFloor, SyncConfig = fullSync };

        // Full-only pruning before the first run — looks archive-equivalent (no floor recorded).
        yield return new CapabilitiesScenario(
            Name: "full_pruning_before_first_run_looks_archive",
            State: FullPrunedState,
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { SyncConfig = fullSync };

        // Full-only pruning after a successful run — floor advances to copied-state block.
        const long fullPruneFloor = 500;
        ResourceAvailability fullPruned = new(Disabled: false, OldestBlock: fullPruneFloor);
        yield return new CapabilitiesScenario(
            Name: "full_pruning_after_run_reports_copied_state_floor",
            State: FullPrunedState,
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
            State: new StateAvailability(RetentionWindowBlocks: retention),
            ExpectedState: memoryPruned, ExpectedStateproofs: memoryPruned,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { HeadNumber = memoryHead, OldestStateBlock = 0, SyncConfig = fullSync };

        // Memory pruning shortly after fast sync: pivot floor is tighter than the rolling window.
        const long recentPivot = 950;
        ResourceAvailability postSyncMemory = new(
            Disabled: false,
            OldestBlock: recentPivot,
            DeleteStrategy: new DeleteStrategy("window", retention));
        yield return new CapabilitiesScenario(
            Name: "memory_pruning_floor_dominates_window",
            State: new StateAvailability(RetentionWindowBlocks: retention),
            ExpectedState: postSyncMemory, ExpectedStateproofs: postSyncMemory,
            ExpectedReceipts: Available, ExpectedBlocks: Available)
        { HeadNumber = memoryHead, OldestStateBlock = recentPivot, SyncConfig = fullSync };

        yield return new CapabilitiesScenario(
            Name: "no_receipts_disables_tx_logs_receipts",
            State: ArchiveState,
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: Disabled, ExpectedBlocks: Available)
        {
            SyncConfig = new SyncConfig { DownloadReceiptsInFastSync = false },
        };

        const long rollingFloor = 1000;
        const uint retentionEpochs = 200;
        ResourceAvailability rollingPruned = new(
            Disabled: false,
            OldestBlock: rollingFloor,
            DeleteStrategy: new DeleteStrategy("window", retentionEpochs * 32));
        yield return new CapabilitiesScenario(
            Name: "rolling_history_pruning_window_and_floor",
            State: ArchiveState,
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
            State: ArchiveState,
            ExpectedState: Available, ExpectedStateproofs: Available,
            ExpectedReceipts: ancientPruned, ExpectedBlocks: ancientPruned)
        {
            SyncConfig = fullSync,
            HistoryConfig = new HistoryConfig { Pruning = PruningModes.UseAncientBarriers },
            HistoryPruner = MockHistoryPruner(ancientFloor),
        };
    }

    [TestCaseSource(nameof(CapabilitiesScenarios))]
    public void eth_capabilities_scenario(CapabilitiesScenario s)
    {
        EthCapabilities caps = GetCaps(s.State, s.HeadNumber, s.OldestStateBlock, s.SyncConfig, s.HistoryConfig, s.HistoryPruner);

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
            new StateAvailability(RetentionWindowBlocks: 64),
            headNumber: 1000,
            syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 });

        string json = new EthereumJsonSerializer().Serialize(caps);
        JsonSchema schema = await JsonSchema.FromJsonAsync(EthCapabilitiesSchema);
        ICollection<ValidationError> errors = schema.Validate(json);

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }
}
