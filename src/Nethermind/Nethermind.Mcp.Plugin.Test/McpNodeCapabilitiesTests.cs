// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
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
using Nethermind.Trie;
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
        node.Pointers.LowestInsertedBodyNumber.Returns(500_000UL);
        node.Pointers.LowestInsertedReceiptBlockNumber.Returns(600_000UL);
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
    public void History_expiry_is_named_when_an_expired_body_is_missing()
    {
        NodeFixture node = new() { History = { Pruning = PruningModes.Rolling } };
        node.HistoryPruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(15_000_000).TestObject);
        node.HistoryPruner.GetRetentionBlocks(Arg.Any<ulong>()).Returns(5_000_000UL);
        McpNodeCapabilities capabilities = node.Create();

        string body = Unavailable(capabilities.CheckBody(1_000));
        McpDataAvailability availability = capabilities.GetAvailability();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("EIP-4444"));
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

    private static string Unavailable(CallToolResult? result)
    {
        Assert.That(result, Is.Not.Null, "expected an unavailable error");
        JsonElement error = McpAssert.Error(result!, McpAssert.Unavailable);
        return error.GetProperty("message").GetString()!;
    }

    /// <summary>Substituted node services; every block exists, and by default no state, body or receipts are stored.</summary>
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

        public McpNodeCapabilities Create()
        {
            BlockHeader head = Header(Head);
            BlockTree.Head.Returns(Build.A.Block.WithHeader(head).TestObject);
            BlockTree.BestSuggestedHeader.Returns(head);
            BlockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(c => UnknownBlocks ? null : Header(c.Arg<ulong>()));

            EthCapabilitiesProvider? provider = WithProvider
                ? new EthCapabilitiesProvider(BlockTree, StateBoundary, Sync, Pointers, History, HistoryPruner)
                : null;
            return new McpNodeCapabilities(BlockTree, Sync, Receipts, Pruning, Flat, Init, LimboLogs.Instance,
                provider, StateReader, ReceiptStorage, worldStateManager: null, SyncingInfo, HistoryPruner);
        }

        private BlockHeader Header(ulong number)
        {
            BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(number);
            return (EmptyBlocks ? builder : builder.WithTransactionsRoot(Keccak.Compute("txs"))).TestObject;
        }
    }
}
