// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    /// <summary>
    /// Builds a chain and returns the result of eth_capabilities with the given configs.
    /// </summary>
    private static async Task<EthCapabilitiesResult> GetCaps(
        SyncConfig syncConfig,
        PruningConfig pruningConfig)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c, syncConfig, pruningConfig))
            .Build();
        return chain.EthRpcModule.eth_capabilities().Data!;
    }

    [Test]
    public async Task eth_capabilities_returns_all_resources_and_head()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.Head, Is.Not.Null);
        Assert.That(caps.Head.Number, Does.StartWith("0x"));
        Assert.That(caps.Head.Hash, Does.StartWith("0x"));

        Assert.That(caps.Blocks, Is.Not.Null);
        Assert.That(caps.State, Is.Not.Null);
        Assert.That(caps.Tx, Is.Not.Null);
        Assert.That(caps.Logs, Is.Not.Null);
        Assert.That(caps.Receipts, Is.Not.Null);
        Assert.That(caps.Stateproofs, Is.Not.Null);
    }

    [Test]
    public async Task eth_capabilities_null_configs_defaults_to_archive_with_receipts()
    {
        // null configs → archive (stateproofs on, no window) + receipts enabled
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.Stateproofs.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.Null);
        Assert.That(caps.Tx.Disabled, Is.False);
        Assert.That(caps.Logs.Disabled, Is.False);
        Assert.That(caps.Receipts.Disabled, Is.False);
    }

    [Test]
    public async Task eth_capabilities_archive_node_full_availability()
    {
        // Covers archive state+stateproofs availability AND the genesis-receipts regression
        // (AncientReceiptsBarrierCalc would return 1 for PivotNumber=0; must be 0x0).
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.None });

        // State: full history, no rolling-window delete strategy
        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.OldestBlock, Is.EqualTo("0x0"));
        Assert.That(caps.State.DeleteStrategy, Is.Null, "Archive node has no delete strategy");

        // Stateproofs: enabled from genesis
        Assert.That(caps.Stateproofs.Disabled, Is.False, "Archive node serves state proofs");
        Assert.That(caps.Stateproofs.OldestBlock, Is.EqualTo("0x0"));

        // Receipts/tx/logs: available from genesis (not 0x1)
        Assert.That(caps.Tx.OldestBlock, Is.EqualTo("0x0"), "Archive tx available from genesis");
        Assert.That(caps.Logs.OldestBlock, Is.EqualTo("0x0"), "Archive logs available from genesis");
        Assert.That(caps.Receipts.OldestBlock, Is.EqualTo("0x0"), "Archive receipts available from genesis");

        // Blocks: headers available
        Assert.That(caps.Blocks.Disabled, Is.False);
        Assert.That(caps.Blocks.OldestBlock, Is.Not.Null);
    }

    [Test]
    public async Task eth_capabilities_memory_pruned_node_reports_window_and_disables_stateproofs()
    {
        const int pruningBoundary = 128;
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = pruningBoundary });

        Assert.That(caps.Stateproofs.Disabled, Is.True, "Pruned node cannot serve historical state proofs");
        Assert.That(caps.Stateproofs.OldestBlock, Is.Null);

        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.Not.Null);
        Assert.That(caps.State.DeleteStrategy!.Type, Is.EqualTo("window"));
        Assert.That(caps.State.DeleteStrategy.RetentionBlocks, Is.EqualTo(pruningBoundary));
    }

    [Test]
    public async Task eth_capabilities_full_pruning_omits_state_oldest_block_and_delete_strategy()
    {
        // Full pruning is periodic and non-linear — no predictable window to report.
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
            new PruningConfig { Mode = PruningMode.Full });

        Assert.That(caps.State.Disabled, Is.False, "State is still available on a full-pruned node");
        Assert.That(caps.State.OldestBlock, Is.Null, "Full-only pruning: oldest block unknown, must not be reported");
        Assert.That(caps.State.DeleteStrategy, Is.Null, "Full-only pruning is not a rolling window");
    }

    [Test]
    public async Task eth_capabilities_no_receipts_disables_tx_logs_and_receipts()
    {
        EthCapabilitiesResult caps = await GetCaps(
            new SyncConfig { DownloadReceiptsInFastSync = false },
            new PruningConfig { Mode = PruningMode.None });

        Assert.That(caps.Tx.Disabled, Is.True);
        Assert.That(caps.Logs.Disabled, Is.True);
        Assert.That(caps.Receipts.Disabled, Is.True);

        Assert.That(caps.Tx.OldestBlock, Is.Null, "Disabled resource has no oldestBlock");
        Assert.That(caps.Logs.OldestBlock, Is.Null);
        Assert.That(caps.Receipts.OldestBlock, Is.Null);
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
