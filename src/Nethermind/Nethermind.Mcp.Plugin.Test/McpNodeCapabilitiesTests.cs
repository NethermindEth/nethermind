// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.History;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.State;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// <see cref="McpNodeCapabilities"/> over substituted services, with the production <see cref="EthCapabilitiesProvider"/>
/// computing the ranges, for archive, pruned, fast-sync ancient-barrier and history-expiry nodes.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class McpNodeCapabilitiesTests
{
    private const ulong Head = 20_000_128;

    [Test]
    public void Archive_node_serves_old_state_and_reports_genesis_floor()
    {
        NodeFixture node = new() { Pruning = { Mode = PruningMode.None } };
        node.StateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(true);
        McpNodeCapabilities capabilities = node.Create();

        McpDataAvailability availability = capabilities.GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.CheckState(1), Is.Null);
            Assert.That(availability.HeadNumber, Is.EqualTo((long)Head));
            Assert.That(availability.OldestStateBlock, Is.Zero);
            Assert.That(availability.StateRetentionBlocks, Is.Null);
            Assert.That(availability.Storage, Is.EqualTo(new McpStateStorage("HalfPath", true, "archive, HalfPath")));
        }
    }

    [Test]
    public void Pruned_node_names_the_state_window_when_old_state_is_missing()
    {
        NodeFixture node = new() { Pruning = { Mode = PruningMode.Hybrid, PruningBoundary = 128 } };
        node.StateBoundary.RetentionWindowBlocks.Returns(128UL);
        node.StateReader.HasStateForBlock(Arg.Is<BlockHeader?>(h => h != null && h.Number >= Head - 128)).Returns(true);
        McpNodeCapabilities capabilities = node.Create();

        string message = Unavailable(capabilities.CheckState(123));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.StartWith("State for block 123 is not available on this node"));
            Assert.That(message, Does.Contain("keeps state for blocks 20000000..20000128"));
            Assert.That(message, Does.Contain("pruned, HalfPath"));
            Assert.That(message, Does.Contain("archive node"));
            Assert.That(capabilities.CheckState((long)Head - 5), Is.Null, "recent state is available");
            Assert.That(capabilities.GetAvailability().StateRetentionBlocks, Is.EqualTo(128));
        }
    }

    [Test]
    public void Hash_scheme_is_reported_from_the_detected_key_scheme()
    {
        NodeFixture node = new() { Init = { StateDbKeyScheme = INodeStorage.KeyScheme.Hash }, Pruning = { Mode = PruningMode.Memory } };

        McpStateStorage? storage = node.Create().GetStateStorage();

        Assert.That(storage?.Backend, Is.EqualTo("Hash"));
        Assert.That(storage?.Archive, Is.False);
    }

    [Test]
    public void State_check_while_state_syncing_says_to_wait_for_the_sync()
    {
        NodeFixture node = new() { Sync = { FastSync = true, SnapSync = true, PivotNumber = Head - 1000 } };
        node.SyncingInfo.IsSyncing().Returns(true);
        McpNodeCapabilities capabilities = node.Create();

        string message = Unavailable(capabilities.CheckState((long)Head));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.GetAvailability().OldestStateBlock, Is.Null, "no state before the state sync writes its floor");
            Assert.That(message, Does.Contain("still syncing"));
            Assert.That(message, Does.Contain("Retry after the node finishes syncing"));
        }
    }

    [Test]
    public void Ancient_body_barrier_is_named_when_an_old_body_is_missing()
    {
        NodeFixture node = new() { Sync = { FastSync = true, SnapSync = true, PivotNumber = 1_000_000, AncientBodiesBarrier = 500_000, AncientReceiptsBarrier = 600_000 } };
        node.BodiesFrom = 500_000;
        node.ReceiptsFrom = 600_000;
        node.StateBoundary.OldestStateBlock.Returns(1_000_000UL);
        McpNodeCapabilities capabilities = node.Create();

        string body = Unavailable(capabilities.CheckBody(100));
        string receipts = Unavailable(capabilities.CheckReceipts(550_000));
        McpDataAvailability availability = capabilities.GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.StartWith("The body (transactions) of block 100 is not stored on this node"));
            Assert.That(body, Does.Contain("keeps bodies for blocks 500000..20000128"));
            Assert.That(body, Does.Contain("Sync.AncientBodiesBarrier"));
            Assert.That(receipts, Does.Contain("keeps receipts for blocks 600000..20000128"));
            Assert.That(receipts, Does.Contain("Sync.AncientReceiptsBarrier"));
            Assert.That(availability.OldestBodyBlock, Is.EqualTo(500_000));
            Assert.That(availability.OldestReceiptBlock, Is.EqualTo(600_000));
            Assert.That(availability.OldestStateBlock, Is.EqualTo(1_000_000));
        }
    }

    [Test]
    public void Transaction_history_limit_names_the_later_of_the_body_and_receipt_floors()
    {
        NodeFixture limited = new() { Sync = { FastSync = true, SnapSync = true, PivotNumber = 1_000_000, AncientBodiesBarrier = 500_000, AncientReceiptsBarrier = 600_000 } };
        limited.BodiesFrom = 500_000;
        limited.ReceiptsFrom = 600_000;
        NodeFixture full = new() { BodiesFrom = 0, ReceiptsFrom = 0 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(limited.Create().DescribeTransactionHistoryLimit(),
                Is.EqualTo(" This node keeps transaction history only from block 600000 (see node_status); older transactions cannot be found here."));
            Assert.That(full.Create().GetAvailability().OldestBodyBlock, Is.Zero, "a stored genesis body means full history");
            Assert.That(full.Create().DescribeTransactionHistoryLimit(), Is.Empty, "full history needs no hint");
            Assert.That(new NodeFixture().Create().DescribeTransactionHistoryLimit(), Is.Empty, "an unknown range needs no hint");
        }
    }

    [Test]
    public void Fast_sync_without_old_history_reports_the_probed_floors_not_the_pivot()
    {
        // The live case: bodies start well above the configured pivot and well below the moving beacon pivot,
        // and the history pruner reports the moving pivot as its oldest block although expiry is disabled.
        NodeFixture node = new()
        {
            Sync = { FastSync = true, SnapSync = true, PivotNumber = 1_000_000, DownloadBodiesInFastSync = false, DownloadReceiptsInFastSync = false },
            BodiesFrom = 1_500_000,
            ReceiptsFrom = 1_600_000,
        };
        node.BlockTree.SyncPivot.Returns((19_999_000UL, Keccak.Compute("beacon pivot")));
        node.HistoryPruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(19_999_000).TestObject);
        McpNodeCapabilities capabilities = node.Create();

        McpDataAvailability availability = capabilities.GetAvailability();
        string body = Unavailable(capabilities.CheckBody(1_000_001));
        string receipts = Unavailable(capabilities.CheckReceipts(1_550_000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.OldestBodyBlock, Is.EqualTo(1_500_000), "eth_capabilities reports these as disabled; the stores hold them");
            Assert.That(availability.OldestReceiptBlock, Is.EqualTo(1_600_000));
            Assert.That(capabilities.CheckBody(1_500_000), Is.Null);
            Assert.That(capabilities.CheckBody(19_000_000), Is.Null, "a block below the moving pivot is served");
            Assert.That(body, Does.Contain("keeps bodies for blocks 1500000..20000128"));
            Assert.That(body, Does.Contain("this node was snap/fast-synced and did not download older bodies (Sync.DownloadBodiesInFastSync=false)"));
            Assert.That(body, Does.Not.Contain("EIP-4444"), "history pruning is not configured");
            Assert.That(receipts, Does.Contain("keeps receipts for blocks 1600000..20000128"));
            Assert.That(receipts, Does.Contain("did not download older receipts (Sync.DownloadBodiesInFastSync=false, Sync.DownloadReceiptsInFastSync=false)"));
            Assert.That(capabilities.DescribeTransactionHistoryLimit(), Does.Contain("from block 1600000"));
        }
    }

    [Test]
    public void Fast_sync_without_old_receipts_keeps_the_downloaded_body_floor()
    {
        NodeFixture node = new()
        {
            Sync = { FastSync = true, SnapSync = true, PivotNumber = 1_000_000, DownloadReceiptsInFastSync = false, AncientBodiesBarrier = 500_000 },
            BodiesFrom = 500_000,
            ReceiptsFrom = 1_000_123,
        };
        McpNodeCapabilities capabilities = node.Create();

        McpDataAvailability availability = capabilities.GetAvailability();
        string receipts = Unavailable(capabilities.CheckReceipts(900_000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.OldestBodyBlock, Is.EqualTo(500_000));
            Assert.That(availability.OldestReceiptBlock, Is.EqualTo(1_000_123));
            Assert.That(receipts, Does.Contain("did not download older receipts (Sync.DownloadReceiptsInFastSync=false)"));
        }
    }

    [Test]
    public void Receipt_floor_skips_blocks_without_transactions()
    {
        // Odd blocks are empty; receipts are stored from 600001, so 600001 (empty) is the first block needing none missing.
        NodeFixture node = new() { BodiesFrom = 1, ReceiptsFrom = 600_001, EmptyWhen = static n => n % 2 == 1 };
        McpNodeCapabilities capabilities = node.Create();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.GetAvailability().OldestReceiptBlock, Is.EqualTo(600_001));
            Assert.That(node.ReceiptStorage.ReceivedCalls().Select(static c => c.GetArguments()[0]).OfType<ulong>().Where(static n => n % 2 == 1), Is.Empty,
                "receipts of an empty block are never probed");
        }
    }

    [Test]
    public void Floor_probe_is_cheap_and_cached()
    {
        NodeFixture node = new() { BodiesFrom = 12_345_678, ReceiptsFrom = 12_345_700 };
        McpNodeCapabilities capabilities = node.Create();

        McpDataAvailability availability = capabilities.GetAvailability();
        int bodyLookups = HasBlockCalls(node.BlockTree);
        int receiptLookups = HasBlockCalls(node.ReceiptStorage);
        capabilities.DescribeTransactionHistoryLimit();
        capabilities.GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.OldestBodyBlock, Is.EqualTo(12_345_678));
            Assert.That(availability.OldestReceiptBlock, Is.EqualTo(12_345_700));
            Assert.That(bodyLookups, Is.LessThanOrEqualTo(30));
            Assert.That(receiptLookups, Is.LessThanOrEqualTo(30));
            Assert.That(HasBlockCalls(node.BlockTree) + HasBlockCalls(node.ReceiptStorage), Is.EqualTo(bodyLookups + receiptLookups), "cached");
        }

        static int HasBlockCalls(object substitute) => substitute.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == "HasBlock");
    }

    [Test]
    public void Missing_block_above_the_cached_floor_probes_again()
    {
        NodeFixture node = new() { BodiesFrom = 500_000 };
        McpNodeCapabilities capabilities = node.Create();
        Assert.That(capabilities.GetAvailability().OldestBodyBlock, Is.EqualTo(500_000));

        node.BodiesFrom = 600_000;
        string body = Unavailable(capabilities.CheckBody(550_000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("keeps bodies for blocks 600000..20000128"));
            Assert.That(capabilities.GetAvailability().OldestBodyBlock, Is.EqualTo(600_000));
        }
    }

    [Test]
    public void Missing_history_without_an_explaining_setting_is_described_neutrally()
    {
        NodeFixture node = new() { BodiesFrom = 1_000 };
        node.HistoryPruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(1_000).TestObject);

        string body = Unavailable(node.Create().CheckBody(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("keeps bodies for blocks 1000..20000128. "));
            Assert.That(body, Does.Not.Contain("EIP-4444"));
            Assert.That(body, Does.Not.Contain("Sync."));
        }
    }

    [Test]
    public void Memory_pruned_state_summary_names_the_actual_retention_window()
    {
        NodeFixture node = new() { Pruning = { Mode = PruningMode.Hybrid, PruningBoundary = 128 } };
        node.StateBoundary.RetentionWindowBlocks.Returns(64UL);

        McpDataAvailability availability = node.Create().GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.StateRetentionBlocks, Is.EqualTo(64));
            Assert.That(availability.Storage?.Summary, Is.EqualTo("pruned, HalfPath: about the last 64 blocks"));
        }
    }

    [Test]
    public void History_expiry_is_named_when_an_expired_body_is_missing()
    {
        NodeFixture node = new() { History = { Pruning = PruningModes.Rolling }, BodiesFrom = 15_000_000 };
        node.HistoryPruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(15_000_000).TestObject);
        node.HistoryPruner.GetRetentionBlocks(Arg.Any<ulong>()).Returns(5_000_000UL);
        McpNodeCapabilities capabilities = node.Create();

        string body = Unavailable(capabilities.CheckBody(1_000));
        McpDataAvailability availability = capabilities.GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("history expiry (EIP-4444, History.Pruning=Rolling) removed blocks below 15000000"));
            Assert.That(body, Does.Contain("keeps bodies for blocks 15000000..20000128"));
            Assert.That(availability.OldestBodyBlock, Is.EqualTo(15_000_000));
            Assert.That(availability.HistoryRetentionBlocks, Is.EqualTo(5_000_000));
        }
    }

    [Test]
    public void Stored_data_passes_every_check()
    {
        NodeFixture node = new();
        node.StateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(true);
        node.BlockTree.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        node.ReceiptStorage.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        McpNodeCapabilities capabilities = node.Create();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.CheckState(10), Is.Null);
            Assert.That(capabilities.CheckBody(10), Is.Null);
            Assert.That(capabilities.CheckReceipts(10), Is.Null);
        }
    }

    [Test]
    public void Unknown_blocks_and_negative_numbers_are_never_rejected()
    {
        NodeFixture node = new() { UnknownBlocks = true };
        McpNodeCapabilities capabilities = node.Create();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.CheckState(Head + 10), Is.Null);
            Assert.That(capabilities.CheckBody(Head + 10), Is.Null);
            Assert.That(capabilities.CheckReceipts(Head + 10), Is.Null);
            Assert.That(capabilities.CheckState(-1), Is.Null);
        }
    }

    [Test]
    public void Failing_services_never_produce_a_false_negative()
    {
        NodeFixture node = new();
        node.StateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(_ => throw new InvalidOperationException("db closed"));
        node.BlockTree.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new InvalidOperationException("db closed"));
        node.ReceiptStorage.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new InvalidOperationException("db closed"));
        McpNodeCapabilities capabilities = node.Create();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.CheckState(10), Is.Null);
            Assert.That(capabilities.CheckBody(10), Is.Null);
            Assert.That(capabilities.CheckReceipts(10), Is.Null);
        }
    }

    [Test]
    public void Missing_capabilities_provider_yields_unknown_ranges()
    {
        NodeFixture node = new() { WithProvider = false };

        McpDataAvailability availability = node.Create().GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.HeadNumber, Is.EqualTo((long)Head));
            Assert.That(availability.OldestStateBlock, Is.Null);
            Assert.That(availability.OldestBodyBlock, Is.Null);
            Assert.That(availability.OldestReceiptBlock, Is.Null);
        }
    }

    [Test]
    public void Receipts_of_a_block_without_transactions_are_never_missing()
    {
        NodeFixture node = new() { EmptyBlocks = true };

        Assert.That(node.Create().CheckReceipts(10), Is.Null);
    }

    [Test]
    public void Disabled_receipt_storage_is_named()
    {
        NodeFixture node = new() { Receipts = { StoreReceipts = false } };
        McpNodeCapabilities capabilities = node.Create();

        string message = Unavailable(capabilities.CheckReceipts(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("Receipt.StoreReceipts=false"));
            Assert.That(capabilities.GetAvailability().ReceiptsStored, Is.False);
            Assert.That(capabilities.GetAvailability().OldestReceiptBlock, Is.Null);
        }
    }

    [Test]
    public void Derived_receipts_are_not_checked_against_the_store()
    {
        NodeFixture node = new() { Receipts = { DeriveFromState = true } };

        Assert.That(node.Create().CheckReceipts(10), Is.Null);
    }

    [Test]
    public void Availability_is_cached_briefly()
    {
        NodeFixture node = new();
        McpNodeCapabilities capabilities = node.Create();

        McpDataAvailability first = capabilities.GetAvailability();
        McpDataAvailability second = capabilities.GetAvailability();

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public async Task Tools_name_the_probed_floors_end_to_end()
    {
        await using McpTestNode node = await McpTestNode.Create();
        ulong first = (await node.Seed()).Block.Number;
        ulong second = (await node.Seed()).Block.Number;
        ulong third = (await node.Seed()).Block.Number;
        IBlockTree blockTree = node.Chain.BlockTree;

        // Bodies are kept from the second seeded block, receipts from the third.
        for (ulong n = 1; n <= second; n++)
        {
            Block block = blockTree.FindBlock(n, BlockTreeLookupOptions.RequireCanonical)!;
            node.Chain.ReceiptStorage.RemoveReceipts(block);
            if (n <= first) blockTree.DeleteOldBlock(n, block.Hash!);
        }

        await using McpClient client = await node.CreateClient();
        string missingBlock = Unavailable(await McpToolCalls.Call(client, "get_block", [("block", first.ToString(CultureInfo.InvariantCulture))]));
        JsonElement missingTx = McpAssert.Error(await McpToolCalls.Call(client, "get_transaction", [("hash", Keccak.Compute("unknown").ToString())]), McpAssert.NotFound);
        JsonElement history = McpAssert.Success(await McpToolCalls.Call(client, "node_status", [])).GetProperty("history");
        ulong head = blockTree.Head!.Number;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingBlock, Does.Contain($"keeps bodies for blocks {second}..{head}"));
            Assert.That(missingTx.GetProperty("message").GetString(), Does.Contain($"keeps transaction history only from block {third}"));
            Assert.That(history.GetProperty("oldestBodyBlock").GetUInt64(), Is.EqualTo(second));
            Assert.That(history.GetProperty("oldestReceiptBlock").GetUInt64(), Is.EqualTo(third));
        }
    }

    private static string Unavailable(CallToolResult? result)
    {
        Assert.That(result, Is.Not.Null, "expected an unavailable error");
        JsonElement error = McpAssert.Error(result!, McpAssert.Unavailable);
        return error.GetProperty("message").GetString()!;
    }

    /// <summary>
    /// Substituted node services; every block header exists, and by default no state, body or receipts are stored.
    /// <see cref="BodiesFrom"/> and <see cref="ReceiptsFrom"/> store bodies (and the genesis body) and receipts from a block up.
    /// </summary>
    private sealed class NodeFixture
    {
        public IReadOnlyBlockTree BlockTree { get; } = Substitute.For<IReadOnlyBlockTree>();
        public IStateReader StateReader { get; } = Substitute.For<IStateReader>();
        public IReceiptStorage ReceiptStorage { get; } = Substitute.For<IReceiptStorage>();
        public IStateBoundary StateBoundary { get; } = Substitute.For<IStateBoundary>();
        public ISyncPointers Pointers { get; } = Substitute.For<ISyncPointers>();
        public IHistoryPruner HistoryPruner { get; } = Substitute.For<IHistoryPruner>();
        public IEthSyncingInfo SyncingInfo { get; } = Substitute.For<IEthSyncingInfo>();
        public SyncConfig Sync { get; } = new();
        public ReceiptConfig Receipts { get; } = new();
        public PruningConfig Pruning { get; } = new();
        public FlatDbConfig Flat { get; } = new();
        public InitConfig Init { get; } = new();
        public HistoryConfig History { get; } = new();
        public bool WithProvider { get; init; } = true;
        public bool EmptyBlocks { get; init; }
        public bool UnknownBlocks { get; init; }
        public Func<ulong, bool>? EmptyWhen { get; init; }
        public ulong? BodiesFrom { get; set; }
        public ulong? ReceiptsFrom { get; set; }

        public McpNodeCapabilities Create()
        {
            BlockHeader head = Header(Head);
            BlockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
            BlockTree.BestSuggestedHeader.Returns(head);
            BlockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(c => UnknownBlocks ? null : Header(c.Arg<ulong>()));
            if (BodiesFrom is not null)
            {
                BlockTree.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(c => c.Arg<ulong>() is var n && (n == 0 || n >= BodiesFrom));
            }

            if (ReceiptsFrom is not null)
            {
                ReceiptStorage.HasBlock(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(c => c.Arg<ulong>() >= ReceiptsFrom);
            }

            EthCapabilitiesProvider? provider = WithProvider
                ? new EthCapabilitiesProvider(BlockTree, StateBoundary, Sync, Pointers, History, HistoryPruner)
                : null;
            return new McpNodeCapabilities(BlockTree, Sync, Receipts, Pruning, Flat, Init, LimboLogs.Instance,
                provider, StateReader, ReceiptStorage, worldStateManager: null, SyncingInfo, HistoryPruner, historyConfig: History);
        }

        private BlockHeader Header(ulong number)
        {
            BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(number);
            bool empty = EmptyBlocks || EmptyWhen?.Invoke(number) == true;
            return (empty ? builder : builder.WithTransactionsRoot(Keccak.Compute("txs"))).TestObject;
        }
    }
}
