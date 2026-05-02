// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.History;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NJsonSchema;
using NJsonSchema.Validation;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private static readonly ResourceAvailability Available = new(Disabled: false, OldestBlock: 0L);
    private static readonly ResourceAvailability Disabled = new(Disabled: true, OldestBlock: null);

    private static async Task<EthCapabilities> GetCaps(
        SyncConfig syncConfig,
        PruningConfig pruningConfig,
        IHistoryConfig? historyConfig = null,
        IHistoryPruner? historyPruner = null)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c, syncConfig, pruningConfig, historyConfig, historyPruner))
            .Build();
        return chain.EthRpcModule.eth_capabilities().Data;
    }

    public sealed record CapabilitiesScenario(
        string Name,
        SyncConfig SyncConfig,
        PruningConfig PruningConfig,
        ResourceAvailability State,
        ResourceAvailability Stateproofs,
        ResourceAvailability Tx,
        ResourceAvailability Logs,
        ResourceAvailability Receipts,
        ResourceAvailability Blocks)
    {
        public IHistoryConfig? HistoryConfig { get; init; }
        public IHistoryPruner? HistoryPruner { get; init; }
        public override string ToString() => Name;
    }

    private static IHistoryPruner MockHistoryPruner(long oldestBlockNumber)
    {
        IHistoryPruner pruner = Substitute.For<IHistoryPruner>();
        pruner.OldestBlockHeader.Returns(Core.Test.Builders.Build.A.BlockHeader.WithNumber(oldestBlockNumber).TestObject);
        return pruner;
    }

    private static IEnumerable<CapabilitiesScenario> CapabilitiesScenarios()
    {
        // Archive — covers the genesis-receipts regression (AncientReceiptsBarrierCalc returns
        // Math.Max(1, …) which is wrong for PivotNumber=0; receipts must be available from 0).
        yield return new CapabilitiesScenario(
            Name: "archive_full_availability",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: Available, Stateproofs: Available,
            Tx: Available, Logs: Available, Receipts: Available,
            Blocks: Available);

        // Full pruning is periodic and non-linear — no predictable window to report.
        yield return new CapabilitiesScenario(
            Name: "full_pruning_omits_oldest_and_strategy",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.Full },
            State: new ResourceAvailability(Disabled: false, OldestBlock: null),
            Stateproofs: Disabled,
            Tx: Available, Logs: Available, Receipts: Available,
            Blocks: Available);

        // No receipts download → tx/logs/receipts unavailable.
        yield return new CapabilitiesScenario(
            Name: "no_receipts_disables_tx_logs_receipts",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = false },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: Available, Stateproofs: Available,
            Tx: Disabled, Logs: Disabled, Receipts: Disabled,
            Blocks: Available);

        // Rolling history pruning (EIP-4444): blocks/tx/logs/receipts share a known retention
        // window expressed in RetentionEpochs * 32 blocks; floor advances to historyPruner cutoff.
        const long rollingFloor = 1000;
        const uint retentionEpochs = 200;
        ResourceAvailability rollingPruned = new(
            Disabled: false,
            OldestBlock: rollingFloor,
            DeleteStrategy: new DeleteStrategy("window", retentionEpochs * 32));
        yield return new CapabilitiesScenario(
            Name: "rolling_history_pruning_window_and_floor",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: Available, Stateproofs: Available,
            Tx: rollingPruned, Logs: rollingPruned, Receipts: rollingPruned,
            Blocks: rollingPruned)
        {
            HistoryConfig = new HistoryConfig { Pruning = PruningModes.Rolling, RetentionEpochs = retentionEpochs },
            HistoryPruner = MockHistoryPruner(rollingFloor),
        };

        // UseAncientBarriers prunes up to ancient barriers — predictable floor but no rolling window.
        const long ancientFloor = 500;
        ResourceAvailability ancientPruned = new(Disabled: false, OldestBlock: ancientFloor);
        yield return new CapabilitiesScenario(
            Name: "ancient_barriers_history_pruning_advances_floor",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: Available, Stateproofs: Available,
            Tx: ancientPruned, Logs: ancientPruned, Receipts: ancientPruned,
            Blocks: ancientPruned)
        {
            HistoryConfig = new HistoryConfig { Pruning = PruningModes.UseAncientBarriers },
            HistoryPruner = MockHistoryPruner(ancientFloor),
        };
    }

    [TestCaseSource(nameof(CapabilitiesScenarios))]
    public async Task eth_capabilities_scenario(CapabilitiesScenario s)
    {
        EthCapabilities caps = await GetCaps(s.SyncConfig, s.PruningConfig, s.HistoryConfig, s.HistoryPruner);

        Assert.That(caps.Head.Number, Is.GreaterThanOrEqualTo(0));
        Assert.That(caps.Head.Hash, Is.Not.EqualTo(Hash256.Zero));

        Assert.That(caps.State, Is.EqualTo(s.State), nameof(caps.State));
        Assert.That(caps.Stateproofs, Is.EqualTo(s.Stateproofs), nameof(caps.Stateproofs));
        Assert.That(caps.Tx, Is.EqualTo(s.Tx), nameof(caps.Tx));
        Assert.That(caps.Logs, Is.EqualTo(s.Logs), nameof(caps.Logs));
        Assert.That(caps.Receipts, Is.EqualTo(s.Receipts), nameof(caps.Receipts));
        Assert.That(caps.Blocks, Is.EqualTo(s.Blocks), nameof(caps.Blocks));
    }

    [Test]
    public async Task eth_capabilities_memory_pruned_reports_window_and_disables_stateproofs()
    {
        // Memory pruning maintains a rolling window of PruningBoundary recent states.
        // OldestBlock for State depends on chain head, so it is not asserted here.
        const int retention = 128;
        EthCapabilities caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = retention });

        Assert.That(caps.Stateproofs, Is.EqualTo(Disabled));
        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.EqualTo(new DeleteStrategy("window", retention)));
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
                    "type": { "type": "string" },
                    "retentionBlocks": { "type": "string", "pattern": "^0x[0-9a-fA-F]+$" }
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
        EthCapabilities caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = 64 });

        string json = new EthereumJsonSerializer().Serialize(caps);
        JsonSchema schema = await JsonSchema.FromJsonAsync(EthCapabilitiesSchema);
        ICollection<ValidationError> errors = schema.Validate(json);

        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));
    }

    private static IEthRpcModule CreateEthRpcModuleWithConfig(
        TestRpcBlockchain c,
        ISyncConfig syncConfig,
        IPruningConfig pruningConfig,
        IHistoryConfig? historyConfig = null,
        IHistoryPruner? historyPruner = null) =>
        new EthRpcModule(
            c.RpcConfig, c.Bridge, c.BlockFinder, c.ReceiptFinder, c.StateReader,
            c.TxPool, c.TxSender, c.TestWallet, c.LogManager, c.SpecProvider,
            c.GasPriceOracle, Substitute.For<IEthSyncingInfo>(),
            c.FeeHistoryOracle ?? new JsonRpc.Modules.Eth.FeeHistory.FeeHistoryOracle(c.BlockTree, c.ReceiptStorage, c.SpecProvider),
            c.ProtocolsManager, c.ForkInfo, c.LogIndexConfig,
            12ul,
            new EthCapabilitiesProvider(c.BlockTree, syncConfig, pruningConfig, historyConfig, historyPruner));
}
