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
    /// Regression test for https://github.com/NethermindEth/nethermind/issues/11260
    /// eth_capabilities should return head block info and all six resource descriptors.
    /// </summary>
    [Test]
    public async Task eth_capabilities_returns_all_resources_and_head()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        ResultWrapper<EthCapabilitiesResult> result = chain.EthRpcModule.eth_capabilities();

        Assert.That(result.Result.ResultType, Is.EqualTo(Core.ResultType.Success));
        EthCapabilitiesResult caps = result.Data!;

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
    public async Task eth_capabilities_with_null_configs_treats_as_archive_with_receipts()
    {
        // When syncConfig and pruningConfig are null (default), the node behaves as archive
        // with receipts enabled — the safe fallback used in test environments.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        // Default = archive: stateproofs enabled, no window deleteStrategy
        Assert.That(caps.Stateproofs.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.Null);

        // Default = receipts synced: tx/logs/receipts enabled
        Assert.That(caps.Tx.Disabled, Is.False);
        Assert.That(caps.Logs.Disabled, Is.False);
        Assert.That(caps.Receipts.Disabled, Is.False);
    }

    [Test]
    public async Task eth_capabilities_archive_node_has_stateproofs_enabled_and_no_delete_strategy()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
                pruningConfig: new PruningConfig { Mode = PruningMode.None }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.OldestBlock, Is.EqualTo("0x0"));
        Assert.That(caps.State.DeleteStrategy, Is.Null, "Archive node has no delete strategy");

        Assert.That(caps.Stateproofs.Disabled, Is.False, "Archive node serves state proofs");
        Assert.That(caps.Stateproofs.OldestBlock, Is.EqualTo("0x0"));

        // Archive node with blocks available: blocks not disabled
        Assert.That(caps.Blocks.Disabled, Is.False);
        Assert.That(caps.Blocks.OldestBlock, Is.Not.Null);
    }

    [Test]
    public async Task eth_capabilities_pruned_node_disables_stateproofs_and_reports_window()
    {
        const int pruningBoundary = 128;

        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
                pruningConfig: new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = pruningBoundary }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.Stateproofs.Disabled, Is.True, "Pruned node cannot serve historical state proofs");
        Assert.That(caps.Stateproofs.OldestBlock, Is.Null);

        Assert.That(caps.State.Disabled, Is.False);
        Assert.That(caps.State.DeleteStrategy, Is.Not.Null);
        Assert.That(caps.State.DeleteStrategy!.Type, Is.EqualTo("window"));
        Assert.That(caps.State.DeleteStrategy.RetentionBlocks, Is.EqualTo(pruningBoundary));
    }

    [Test]
    public async Task eth_capabilities_full_pruning_only_omits_state_oldest_block()
    {
        // PruningMode.Full is periodic (non-linear), so we cannot claim a predictable
        // oldestBlock. The implementation omits it rather than reporting a misleading value.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
                pruningConfig: new PruningConfig { Mode = PruningMode.Full }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.State.Disabled, Is.False, "State is still available on a full-pruned node");
        Assert.That(caps.State.OldestBlock, Is.Null, "Full-only pruning: oldest block unknown, must not be reported");
        Assert.That(caps.State.DeleteStrategy, Is.Null, "Full-only pruning is not a rolling window");
    }

    [Test]
    public async Task eth_capabilities_no_receipts_disables_tx_logs_receipts()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = false },
                pruningConfig: new PruningConfig { Mode = PruningMode.None }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.Tx.Disabled, Is.True);
        Assert.That(caps.Logs.Disabled, Is.True);
        Assert.That(caps.Receipts.Disabled, Is.True);

        Assert.That(caps.Tx.OldestBlock, Is.Null, "Disabled resource has no oldestBlock");
        Assert.That(caps.Logs.OldestBlock, Is.Null);
        Assert.That(caps.Receipts.OldestBlock, Is.Null);
    }

    [Test]
    public async Task eth_capabilities_archive_node_receipts_oldest_block_is_genesis_not_one()
    {
        // Regression: AncientReceiptsBarrierCalc returns Math.Max(1, 0) = 1 for archive nodes.
        // eth_capabilities must report 0x0 (genesis) when PivotNumber = 0.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
                pruningConfig: new PruningConfig { Mode = PruningMode.None }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;

        Assert.That(caps.Tx.OldestBlock, Is.EqualTo("0x0"), "Archive node tx available from genesis");
        Assert.That(caps.Logs.OldestBlock, Is.EqualTo("0x0"), "Archive node logs available from genesis");
        Assert.That(caps.Receipts.OldestBlock, Is.EqualTo("0x0"), "Archive node receipts available from genesis");
    }

    [Test]
    public async Task eth_capabilities_serializes_to_spec_compliant_json_key_names()
    {
        // Guards against naming-policy regressions: all JSON keys must match the spec
        // (ethereum/execution-apis#755) exactly. EthereumJsonSerializer uses camelCase.
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithEthRpcModule(c => CreateEthRpcModuleWithConfig(c,
                syncConfig: new SyncConfig { DownloadReceiptsInFastSync = true, PivotNumber = 0 },
                pruningConfig: new PruningConfig { Mode = PruningMode.Memory, PruningBoundary = 64 }))
            .Build();

        EthCapabilitiesResult caps = chain.EthRpcModule.eth_capabilities().Data!;
        string json = new EthereumJsonSerializer().Serialize(caps);

        // Top-level resource keys
        Assert.That(json, Does.Contain("\"head\""));
        Assert.That(json, Does.Contain("\"state\""));
        Assert.That(json, Does.Contain("\"tx\""));
        Assert.That(json, Does.Contain("\"logs\""));
        Assert.That(json, Does.Contain("\"receipts\""));
        Assert.That(json, Does.Contain("\"blocks\""));
        Assert.That(json, Does.Contain("\"stateproofs\""));

        // Head field names (renamed from blockNumber/blockHash per s1na's review)
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
