// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    /// <summary>Builds a chain and returns the result of eth_capabilities with the given configs.</summary>
    private static async Task<EthCapabilitiesResult> GetCaps(SyncConfig syncConfig, PruningConfig pruningConfig)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c, syncConfig, pruningConfig))
            .Build();
        return chain.EthRpcModule.eth_capabilities().Data;
    }

    [Test]
    public async Task eth_capabilities_returns_head_and_resources_on_default_build()
    {
        // Default TestRpcBlockchain build is archive with full receipts: nothing should be disabled.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data;

        Assert.That(caps.Head.Number, Is.GreaterThanOrEqualTo(0));
        Assert.That(caps.Head.Hash, Is.Not.EqualTo(Hash256.Zero));

        Assert.That(caps.Stateproofs.Disabled, Is.False);
        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.Null);
        Assert.That(caps.Tx.Disabled, Is.False);
        Assert.That(caps.Logs.Disabled, Is.False);
        Assert.That(caps.Receipts.Disabled, Is.False);
        Assert.That(caps.Blocks.Disabled, Is.False);
        Assert.That(caps.Blocks.OldestBlock, Is.Not.Null);
    }

    public sealed record CapabilitiesScenario(
        string Name,
        SyncConfig SyncConfig,
        PruningConfig PruningConfig,
        CapabilityResource State,
        CapabilityResource Stateproofs,
        CapabilityResource Tx,
        CapabilityResource Logs,
        CapabilityResource Receipts)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<CapabilitiesScenario> CapabilitiesScenarios()
    {
        // Archive — covers the genesis-receipts regression (AncientReceiptsBarrierCalc returns
        // Math.Max(1, …) which is wrong for PivotNumber=0; receipts must be available from 0).
        yield return new CapabilitiesScenario(
            Name: "archive_full_availability",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Stateproofs: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Tx: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Logs: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Receipts: new CapabilityResource(Disabled: false, OldestBlock: 0L));

        // Full pruning is periodic and non-linear — no predictable window to report.
        yield return new CapabilitiesScenario(
            Name: "full_pruning_omits_oldest_and_strategy",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            PruningConfig: new PruningConfig { Mode = PruningMode.Full },
            State: new CapabilityResource(Disabled: false, OldestBlock: null, DeleteStrategy: null),
            Stateproofs: new CapabilityResource(Disabled: true, OldestBlock: null),
            Tx: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Logs: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Receipts: new CapabilityResource(Disabled: false, OldestBlock: 0L));

        // No receipts download → tx/logs/receipts unavailable.
        yield return new CapabilitiesScenario(
            Name: "no_receipts_disables_tx_logs_receipts",
            SyncConfig: new SyncConfig { DownloadReceiptsInFastSync = false },
            PruningConfig: new PruningConfig { Mode = PruningMode.None },
            State: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Stateproofs: new CapabilityResource(Disabled: false, OldestBlock: 0L),
            Tx: new CapabilityResource(Disabled: true, OldestBlock: null),
            Logs: new CapabilityResource(Disabled: true, OldestBlock: null),
            Receipts: new CapabilityResource(Disabled: true, OldestBlock: null));
    }

    [TestCaseSource(nameof(CapabilitiesScenarios))]
    public async Task eth_capabilities_scenario(CapabilitiesScenario s)
    {
        EthCapabilitiesResult caps = await GetCaps(s.SyncConfig, s.PruningConfig);

        Assert.That(caps.State, Is.EqualTo(s.State), nameof(caps.State));
        Assert.That(caps.Stateproofs, Is.EqualTo(s.Stateproofs), nameof(caps.Stateproofs));
        Assert.That(caps.Tx, Is.EqualTo(s.Tx), nameof(caps.Tx));
        Assert.That(caps.Logs, Is.EqualTo(s.Logs), nameof(caps.Logs));
        Assert.That(caps.Receipts, Is.EqualTo(s.Receipts), nameof(caps.Receipts));
    }

    [Test]
    public async Task eth_capabilities_memory_pruned_reports_window_and_disables_stateproofs()
    {
        // Memory pruning maintains a rolling window of PruningBoundary recent states.
        // OldestBlock for State depends on chain head, so it is not asserted here.
        const int retention = 128;
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = retention });

        Assert.That(caps.Stateproofs, Is.EqualTo(new CapabilityResource(Disabled: true, OldestBlock: null)));

        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.EqualTo(new CapabilityDeleteStrategy("window", retention)));
    }

    [Test]
    public async Task eth_capabilities_json_keys_match_spec()
    {
        // Guards against naming-policy regressions: all JSON keys must match
        // ethereum/execution-apis#755 exactly (camelCase via EthereumJsonSerializer).
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = 64 });

        string json = new EthereumJsonSerializer().Serialize(caps);

        // Top-level resource keys
        Assert.That(json, Does.Contain("\"head\""));
        Assert.That(json, Does.Contain("\"state\""));
        Assert.That(json, Does.Contain("\"tx\""));
        Assert.That(json, Does.Contain("\"logs\""));
        Assert.That(json, Does.Contain("\"receipts\""));
        Assert.That(json, Does.Contain("\"blocks\""));
        Assert.That(json, Does.Contain("\"stateproofs\""));

        // Head fields (renamed from blockNumber/blockHash per spec)
        Assert.That(json, Does.Contain("\"number\""));
        Assert.That(json, Does.Contain("\"hash\""));
        Assert.That(json, Does.Not.Contain("\"blockNumber\""));
        Assert.That(json, Does.Not.Contain("\"blockHash\""));

        // Resource field names
        Assert.That(json, Does.Contain("\"disabled\""));
        Assert.That(json, Does.Contain("\"oldestBlock\""));
        Assert.That(json, Does.Contain("\"deleteStrategy\""));
        Assert.That(json, Does.Contain("\"type\""));
        Assert.That(json, Does.Contain("\"retentionBlocks\""));
    }

    private static IEthRpcModule CreateEthRpcModuleWithConfig(
        TestRpcBlockchain c,
        ISyncConfig syncConfig,
        IPruningConfig pruningConfig) =>
        new EthRpcModule(
            c.RpcConfig, c.Bridge, c.BlockFinder, c.ReceiptFinder, c.StateReader,
            c.TxPool, c.TxSender, c.TestWallet, c.LogManager, c.SpecProvider,
            c.GasPriceOracle, Substitute.For<IEthSyncingInfo>(),
            c.FeeHistoryOracle ?? new JsonRpc.Modules.Eth.FeeHistory.FeeHistoryOracle(c.BlockTree, c.ReceiptStorage, c.SpecProvider),
            c.ProtocolsManager, c.ForkInfo, c.LogIndexConfig,
            12ul,
            syncConfig, pruningConfig);
}
