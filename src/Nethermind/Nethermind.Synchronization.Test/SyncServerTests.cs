// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test;

// TODO: add tests for new block notification for eth/69
[Parallelizable(ParallelScope.All)]
public class SyncServerTests
{
    [Test]
    public void When_finding_hash_it_does_not_load_headers()
    {
        Context ctx = new();
        ctx.BlockTree.FindHash(123).Returns(TestItem.KeccakA);
        Hash256 result = ctx.SyncServer.FindHash(123)!;

        ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>());
        ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>());
        ctx.BlockTree.DidNotReceive().FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>());
        Assert.That(result, Is.EqualTo(TestItem.KeccakA));
    }

    [Test]
    public void Receipts_are_served_with_a_single_block_load([Values] bool bodyStored, [Values] bool hasTransactions)
    {
        Context ctx = new();
        BlockHeader header = Build.A.BlockHeader
            .WithTransactionsRoot(hasTransactions ? TestItem.KeccakB : Keccak.EmptyTreeHash)
            .TestObject;
        Hash256 hash = header.Hash!;
        TxReceipt[] stored = [Build.A.Receipt.TestObject];
        ctx.BlockTree.FindHeader(hash, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        ctx.BlockTree.HasBlock(header.Number, hash).Returns(bodyStored);
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(hash).Returns(stored);
        SyncServer server = ctx.CreateSyncServer(ctx.BlockTree, receiptFinder: receiptFinder);

        TxReceipt[]? served = server.GetReceipts(hash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(served, bodyStored ? (hasTransactions ? Is.SameAs(stored) : Is.Empty) : Is.Null);
            ctx.BlockTree.DidNotReceiveWithAnyArgs().FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>());
            receiptFinder.Received(hasTransactions && bodyStored ? 1 : 0).Get(hash);
        }
    }

    [Test]
    public void Receipts_are_not_served_for_an_unknown_block()
    {
        Context ctx = new();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        SyncServer server = ctx.CreateSyncServer(ctx.BlockTree, receiptFinder: receiptFinder);
        using ArrayPoolList<byte> destination = new(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.GetReceipts(TestItem.KeccakA), Is.Null);
            Assert.That(server.TryAppendReceipts(TestItem.KeccakA, new ReceiptMessageDecoder(), withBloom: true, destination), Is.False);
            Assert.That(destination, Is.Empty);
            receiptFinder.DidNotReceiveWithAnyArgs().Get(Arg.Any<Hash256>());
        }
    }

    [Test]
    public void Appended_receipts_of_a_block_without_transactions_are_an_empty_list_when_its_body_is_stored([Values] bool bodyStored)
    {
        Context ctx = new();
        BlockHeader header = Build.A.BlockHeader.WithTransactionsRoot(Keccak.EmptyTreeHash).TestObject;
        Hash256 hash = header.Hash!;
        ctx.BlockTree.FindHeader(hash, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        ctx.BlockTree.HasBlock(header.Number, hash).Returns(bodyStored);
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        // The receipt store keeps no receipts for a block without transactions.
        receiptFinder.Get(hash).Returns([]);
        SyncServer server = ctx.CreateSyncServer(ctx.BlockTree, receiptFinder: receiptFinder);
        using ArrayPoolList<byte> destination = new(0);

        bool appended = server.TryAppendReceipts(hash, new ReceiptMessageDecoder(), withBloom: true, destination);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(appended, Is.EqualTo(bodyStored));
            Assert.That(destination.AsSpan().ToArray(), Is.EqualTo(bodyStored ? new[] { Rlp.EmptyListByte } : Array.Empty<byte>()));
        }
    }

    [Test]
    public void Appended_receipts_are_the_stored_copy_or_encoded_as_receipts_messages_encode_them(
        [Values] bool withBloom, [Values] bool storedFormReadable, [Values(1UL, 4_370_000UL)] ulong number)
    {
        Context ctx = new();
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithTransactionsRoot(TestItem.KeccakB).TestObject;
        Hash256 hash = header.Hash!;
        TxReceipt[] receipts =
        [
            Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(number).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(number).WithTxType(TxType.EIP1559).WithLogs(Build.A.LogEntry.TestObject).TestObject,
        ];
        ctx.BlockTree.FindHeader(hash, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        ctx.BlockTree.HasBlock(header.Number, hash).Returns(true);
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(hash).Returns(receipts);
        byte[] storedCopy = [0xc1, 0x80];
        receiptFinder.TryAppendNetworkReceipts(header, withBloom, Arg.Any<ArrayPoolList<byte>>()).Returns(call =>
        {
            if (!storedFormReadable) return false;
            call.ArgAt<ArrayPoolList<byte>>(2).AddRange(storedCopy);
            return true;
        });
        SyncServer server = ctx.CreateSyncServer(ctx.BlockTree, receiptFinder: receiptFinder);
        IRlpDecoder<TxReceipt> decoder = withBloom ? new ReceiptMessageDecoder() : new ReceiptMessageDecoder69();
        using ArrayPoolList<byte> destination = new(1) { 0xff };

        bool appended = server.TryAppendReceipts(hash, decoder, withBloom, destination);

        byte[] expected = storedFormReadable ? storedCopy : EncodeAsReceiptsMessage(receipts, withBloom);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(appended, Is.True);
            Assert.That(destination.AsSpan().ToArray(), Is.EqualTo((byte[])[0xff, .. expected]));
            receiptFinder.Received(1).TryAppendNetworkReceipts(header, withBloom, Arg.Any<ArrayPoolList<byte>>());
        }
    }

    /// <summary>The receipt sequence of one block as the receipts message serializers write decoded receipts.</summary>
    private static byte[] EncodeAsReceiptsMessage(TxReceipt[] receipts, bool withBloom)
    {
        ReceiptsMessageSerializer serializer = withBloom
            ? new ReceiptsMessageSerializer(MainnetSpecProvider.Instance)
            : new ReceiptsMessageInnerSerializer69(MainnetSpecProvider.Instance);
        using ReceiptsMessage message = new(new ArrayPoolList<TxReceipt[]>(1) { receipts });
        byte[] serialized = new byte[serializer.GetLength(message)];
        serializer.Serialize(serialized, message);
        return new RlpReader(serialized).EnterSequence(out _).ReadEncodedItem().ToArray();
    }

    [Test]
    public void Does_not_request_peer_refresh_on_known_hints()
    {
        Context ctx = new();
        ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(true);
        ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
        ctx.PeerPool.DidNotReceiveWithAnyArgs().RefreshTotalDifficulty(null!, null!);
    }

    [Test]
    public void Requests_peer_refresh_on_unknown_hints()
    {
        Context ctx = new();
        ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(false);
        ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
        ctx.PeerPool.Received().ReceivedWithAnyArgs();
    }

    [Test]
    public void Stops_block_gossip_subscriptions_on_new_head_when_policy_forbids_block_gossip()
    {
        Context ctx = new(Policy.NoBlockGossip);
        BlockEventArgs newHead = new(Build.A.Block.WithNumber(1).TestObject);

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(newHead);

        using (Assert.EnterMultipleScope())
        {
            ctx.BlockTree.Received(1).NewHeadBlock -= Arg.Any<EventHandler<BlockEventArgs>>();
            ctx.PeerPool.Received(1).NotifyPeerBlock -= Arg.Any<EventHandler<PeerBlockNotificationEventArgs>>();
            _ = ctx.BlockTree.DidNotReceive().BestSuggestedHeader;
        }

        ctx.SyncServer.Dispose();
        ctx.PeerPool.Received(1).NotifyPeerBlock -= Arg.Any<EventHandler<PeerBlockNotificationEventArgs>>();
    }

    [Test]
    public void Finds_stored_block_rlp_by_its_block_number([Values] bool headerKnown)
    {
        Context ctx = new();
        byte[] blockRlp = [0xc0];
        if (headerKnown)
        {
            ctx.HeaderStore.GetBlockNumber(TestItem.KeccakA).Returns(5UL);
        }
        ctx.BlockStore.GetOwnedRlp(5, TestItem.KeccakA).Returns(ArrayMemoryManager.From(blockRlp));

        using MemoryManager<byte>? found = ctx.SyncServer.FindBlockRlp(TestItem.KeccakA);
        Assert.That(found?.Memory.ToArray(), Is.EqualTo(headerKnown ? blockRlp : null));
    }

    [Test]
    public void Prefetches_the_known_hashes_in_the_window([Values] bool receipts)
    {
        Context ctx = new();
        using PrefetchingTestDb blockDb = new();
        using PrefetchingTestDb headerDb = new();
        HeaderStore headerStore = new(headerDb, new MemDb());
        Block known = Build.A.Block.WithNumber(5).TestObject;
        Block outside = Build.A.Block.WithNumber(9).TestObject;
        headerStore.InsertBlockNumber(known.Hash!, known.Number);
        headerStore.InsertBlockNumber(outside.Hash!, outside.Number);
        SyncServer server = ctx.CreateSyncServer(ctx.BlockTree, headerStore: headerStore, blockStore: new BlockStore(blockDb));

        Action<IReadOnlyList<Hash256>, int, int> prefetch = receipts ? server.PrefetchReceipts : server.PrefetchBlocks;

        prefetch([outside.Hash!, known.Hash!, TestItem.KeccakA, outside.Hash!], 1, 2);

        byte[] knownKey = Bytes.Concat(known.Number.ToBigEndianByteArray(), known.Hash!.BytesToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That((receipts ? headerDb : blockDb).Prefetches, OperatingSystem.IsLinux() ? Is.EqualTo(new[] { new[] { knownKey } }) : Is.Empty);
            Assert.That((receipts ? blockDb : headerDb).Prefetches, Is.Empty);
        }
    }

    [TestCase(true, true, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    public void Can_accept_new_valid_blocks(bool sealOk, bool validationOk, bool accepted)
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

        ISealValidator sealValidator = sealOk ? Always.Valid : Always.Invalid;
        IBlockValidator blockValidator = validationOk ? Always.Valid : Always.Invalid;
        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree, blockValidator, sealValidator);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;

        if (!accepted)
        {
            Assert.Throws<EthSyncException>(() => ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock));
        }
        else
        {
            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        }

        if (accepted)
        {
            Assert.That(block.Header, Is.EqualTo(localBlockTree.BestSuggestedHeader));
        }
        else
        {
            Assert.That(block.Header, Is.Not.EqualTo(localBlockTree.BestSuggestedHeader));
        }
    }

    [TestCase(SyncMode.FastSync, false)]
    [TestCase(SyncMode.StateNodes, false)]
    [TestCase(SyncMode.Full, true)]
    public void Should_accept_or_not_blocks_depends_on_sync_mode(SyncMode syncMode, bool expectBlockAccepted)
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        StaticSelector staticSelector = syncMode switch
        {
            SyncMode.FastSync => StaticSelector.FastSync,
            SyncMode.StateNodes => StaticSelector.StateNodesWithFastBlocks,
            _ => StaticSelector.Full,
        };
        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree, syncModeSelector: staticSelector);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

        Assert.That(block.Header.Equals(localBlockTree.BestSuggestedHeader), Is.EqualTo(expectBlockAccepted));
    }

    [Test]
    public void Terminal_block_with_lower_td_should_not_change_best_suggested_but_should_be_added_to_block_tree()
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        TestSpecProvider testSpecProvider = new(London.Instance)
        {
            TerminalTotalDifficulty = 10_000_000,
        };

        Block newBestLocalBlock = Build.A.Block
            .WithNumber(localBlockTree.Head!.Number + 1)
            .WithParent(localBlockTree.Head!).WithDifficulty(10_000_002L).TestObject;
        localBlockTree.SuggestBlock(newBestLocalBlock);

        PoSSwitcher poSSwitcher = new(
            new MergeConfig
            {
                TerminalTotalDifficulty = $"{testSpecProvider.TerminalTotalDifficulty}"
            },
            new TestSyncConfig(),
            new MemDb(),
            localBlockTree,
            testSpecProvider,
            new ChainSpec(),
            LimboLogs.Instance);
        HeaderValidator headerValidator = new(
            localBlockTree,
            Always.Valid,
            testSpecProvider,
            LimboLogs.Instance);

        MergeHeaderValidator mergeHeaderValidator = new(
            poSSwitcher,
            headerValidator,
            localBlockTree,
            testSpecProvider,
            Always.Valid,
            LimboLogs.Instance);
        BlockValidator blockValidator = new(
            Always.Valid,
            mergeHeaderValidator,
            Always.Valid,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        ctx.SyncServer = new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            localBlockTree,
            Substitute.For<IBlockStore>(),
            Substitute.For<IHeaderStore>(),
            new HeaderDecoder(),
            NullReceiptStorage.Instance,
            Substitute.For<IBlockAccessListStore>(),
            blockValidator,
            Always.Valid,
            ctx.PeerPool,
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            ctx.HistoryPruner,
            testSpecProvider,
            LimboLogs.Instance);

        Block? remoteBestBlock = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

        ctx.SyncServer.AddNewBlock(remoteBestBlock!, ctx.NodeWhoSentTheBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(localBlockTree.BestSuggestedHeader!.Hash, Is.EqualTo(newBestLocalBlock.Header.Hash));
            Assert.That(localBlockTree.FindBlock(remoteBestBlock!.Hash, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(remoteBestBlock.Hash));
        }
    }

    [Test]
    public void Fake_total_difficulty_from_peer_does_not_trick_the_node([Values(10000000, 20000000)] long ttd)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        Context ctx = CreateMergeContext(9, (UInt256)ttd);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        block.Header.TotalDifficulty = block.Header.TotalDifficulty * 2;

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        Assert.That(block.Header.Hash, Is.EqualTo(ctx.LocalBlockTree.BestSuggestedHeader!.Hash));

        Block parentBlock = remoteBlockTree.FindBlock(8, BlockTreeLookupOptions.None)!;
        Assert.That(ctx.LocalBlockTree.BestSuggestedHeader.TotalDifficulty, Is.EqualTo(parentBlock.TotalDifficulty + block.Difficulty));
    }

    [TestCase(9000000, true)]
    [TestCase(9000000, false)]
    [TestCase(8000010, true)]
    public void Can_reject_block_POW_block_after_TTD(long ttd, bool sendFakeTd)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        Context ctx = CreateMergeContext(9, (UInt256)ttd);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        ApplyFakeTotalDifficulty(block, sendFakeTd);

        Assert.Throws<EthSyncException>(() => ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock));
        Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(8));
    }

    [TestCase(9000000, true)]
    [TestCase(9000000, false)]
    [TestCase(8000010, true)]
    public void Post_merge_blocks_wont_be_accepted_by_gossip(long ttd, bool sendFakeTd)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        Block postMergeBlock = Build.A.Block
            .WithDifficulty(0)
            .WithParent(remoteBlockTree.Head!)
            .WithTotalDifficulty(remoteBlockTree.Head!.TotalDifficulty)
            .WithNonce(0u)
            .TestObject;
        remoteBlockTree.SuggestBlock(postMergeBlock);
        Context ctx = CreateMergeContext(9, (UInt256)ttd);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        ApplyFakeTotalDifficulty(block, sendFakeTd);

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(8));
            Assert.That(ctx.LocalBlockTree.FindBlock(postMergeBlock.Hash!, BlockTreeLookupOptions.None), Is.Null);
        }
    }

    [TestCase(9000010, true, 100)]
    [TestCase(9000010, false, 100)]
    [TestCase(9000010, false, 1000000)]
    [TestCase(9000010, true, 1000000)]
    public void Can_inject_terminal_block_with_not_higher_td_than_head(long ttd, bool sendFakeTd, long difficulty)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        Block terminalBlockWithLowerDifficulty = Build.A.Block
            .WithDifficulty((UInt256)difficulty)
            .WithParent(remoteBlockTree.Head!)
            .WithGasLimit(remoteBlockTree.Head!.GasLimit + 1)
            .WithTotalDifficulty(remoteBlockTree.Head.TotalDifficulty + (UInt256)difficulty)
            .TestObject;
        remoteBlockTree.SuggestBlock(terminalBlockWithLowerDifficulty);
        Context ctx = CreateMergeContext(10, (UInt256)ttd);
        Assert.That(terminalBlockWithLowerDifficulty.IsTerminalBlock(ctx.SpecProvider), Is.True);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        ApplyFakeTotalDifficulty(block, sendFakeTd);

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(9));
            Assert.That(ctx.LocalBlockTree.FindBlock(terminalBlockWithLowerDifficulty.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Hash, Is.Not.EqualTo(terminalBlockWithLowerDifficulty.Hash!));
        }
    }

    [TestCase(9000010, true)]
    [TestCase(9000010, false)]
    public void Can_inject_terminal_block_with_higher_td_than_head(long ttd, bool sendFakeTd)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        Block terminalBlockWithHigherTotalDifficulty = Build.A.Block
            .WithDifficulty(1000010)
            .WithParent(remoteBlockTree.Head!)
            .WithTotalDifficulty(remoteBlockTree.Head!.TotalDifficulty + 1000010)
            .TestObject;
        remoteBlockTree.SuggestBlock(terminalBlockWithHigherTotalDifficulty);
        Context ctx = CreateMergeContext(10, (UInt256)ttd);
        Assert.That(terminalBlockWithHigherTotalDifficulty.IsTerminalBlock(ctx.SpecProvider), Is.True);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        ApplyFakeTotalDifficulty(block, sendFakeTd);

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(9));
            Assert.That(ctx.LocalBlockTree.FindBlock(terminalBlockWithHigherTotalDifficulty.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Hash, Is.EqualTo(terminalBlockWithHigherTotalDifficulty.Hash!));
        }
    }


    [TestCase(9000010)]
    public void PostMerge_block_from_gossip_should_not_override_main_chain(long ttd)
    {
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        Block poWBlockPostMerge = Build.A.Block
            .WithDifficulty(1000010)
            .WithParent(remoteBlockTree.Head!)
            .WithTotalDifficulty(remoteBlockTree.Head!.TotalDifficulty + 1000010)
            .TestObject;
        remoteBlockTree.SuggestBlock(poWBlockPostMerge);

        Context ctx = CreateMergeContext(10, (UInt256)ttd);
        Block newPostMergeBlock = Build.A.Block
            .WithDifficulty(0)
            .WithParent(ctx.LocalBlockTree.Head!)
            .WithTotalDifficulty(ctx.LocalBlockTree.Head!.TotalDifficulty)
            .TestObject;
        ctx.LocalBlockTree.SuggestBlock(newPostMergeBlock);
        ctx.LocalBlockTree.TryUpdateMainChain(newPostMergeBlock.Header, true, true, preloadedBlocks: new[] { newPostMergeBlock });

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(10));
            Assert.That(ctx.LocalBlockTree.FindBlock(poWBlockPostMerge.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Hash, Is.EqualTo(newPostMergeBlock.Hash!));
            Assert.That(ctx.LocalBlockTree.FindCanonicalBlockInfo(poWBlockPostMerge.Number)!.BlockHash, Is.Not.EqualTo(poWBlockPostMerge.Hash!));
        }
    }


    private Context CreateMergeContext(int blockTreeChainLength, UInt256 ttd)
    {
        Context ctx = new();
        TestSpecProvider testSpecProvider = new(London.Instance)
        {
            TerminalTotalDifficulty = ttd,
        };
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree localBlockTree = Build.A.BlockTree(genesis, testSpecProvider).OfChainLength(blockTreeChainLength).TestObject;

        PoSSwitcher poSSwitcher = new(
            new MergeConfig
            {
                TerminalTotalDifficulty = $"{ttd}"
            },
            new TestSyncConfig(),
            new MemDb(),
            localBlockTree,
            testSpecProvider,
            new ChainSpec(),
            LimboLogs.Instance);
        MergeSealer mergeSealer = new(new NethDevSealEngine(), poSSwitcher);
        MergeSealValidator mergeSealValidator = new(poSSwitcher, Always.Valid);
        SealEngine sealEngine = new(mergeSealer, mergeSealValidator);
        HeaderValidator headerValidator = new(
            localBlockTree,
            sealEngine,
            testSpecProvider,
            LimboLogs.Instance);
        MergeHeaderValidator mergeHeaderValidator = new(
            poSSwitcher,
            headerValidator,
            localBlockTree,
            testSpecProvider,
            Always.Valid,
            LimboLogs.Instance);

        InvalidChainTracker invalidChainTracker = new(
            poSSwitcher,
            localBlockTree,
            new BlockCacheService(),
            LimboLogs.Instance);
        InvalidHeaderInterceptor headerValidatorWithInterceptor = new(
            mergeHeaderValidator,
            invalidChainTracker,
            LimboLogs.Instance);
        BlockValidator blockValidator = new(
            Always.Valid,
            headerValidatorWithInterceptor,
            Always.Valid,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        ctx.SyncServer = new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            localBlockTree,
            Substitute.For<IBlockStore>(),
            Substitute.For<IHeaderStore>(),
            new HeaderDecoder(),
            NullReceiptStorage.Instance,
            Substitute.For<IBlockAccessListStore>(),
            blockValidator,
            sealEngine,
            ctx.PeerPool,
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            ctx.HistoryPruner,
            testSpecProvider,
            LimboLogs.Instance);
        ctx.SpecProvider = testSpecProvider;
        ctx.LocalBlockTree = localBlockTree;

        return ctx;
    }


    [Test]
    public void Will_not_reject_block_with_bad_total_diff_but_will_reset_diff_to_null()
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

        HeaderValidator headerValidator = new(
            localBlockTree,
            Always.Valid,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        BlockValidator blockValidator = new(
            Always.Valid,
            headerValidator,
            Always.Valid,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree, blockValidator);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;
        block.Header.TotalDifficulty = block.Header.TotalDifficulty * 2;

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
        Assert.That(block.Header.Hash, Is.EqualTo(localBlockTree.BestSuggestedHeader!.Hash));

        Block? parentBlock = remoteBlockTree.FindBlock(8, BlockTreeLookupOptions.None);
        Assert.That(localBlockTree.BestSuggestedHeader.TotalDifficulty, Is.EqualTo(parentBlock!.TotalDifficulty + block.Difficulty));
    }

    [Test]
    public void Rejects_new_old_blocks()
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree, sealValidator: sealValidator);

        Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None)!;

        ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

        sealValidator.DidNotReceive().ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Broadcast_NewBlock_on_arrival()
    {
        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree);

        ISyncServer remoteServer1 = Substitute.For<ISyncServer>();
        SyncPeerMock syncPeerMock1 = CreateSyncPeerMock(remoteBlockTree, remoteServer1, TestItem.PublicKeyA);
        PeerInfo peer1 = new(syncPeerMock1);
        ISyncServer remoteServer2 = Substitute.For<ISyncServer>();
        SyncPeerMock syncPeerMock2 = CreateSyncPeerMock(remoteBlockTree, remoteServer2, TestItem.PublicKeyB);
        PeerInfo peer2 = new(syncPeerMock2);
        ConfigurePeers(ctx, [peer1, peer2]);
        ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peer1.SyncPeer);
        ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peer2.SyncPeer);
        // Notifications fire on a separate task, and one that starts after Close() is dropped, so wait for the delivery.
        await Wait.ForCondition(() => remoteServer2.ReceivedCalls().Any(static c => c.GetMethodInfo().Name == nameof(ISyncServer.AddNewBlock)), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));
        await Task.WhenAll(syncPeerMock1.Close(), syncPeerMock2.Close());
        remoteServer1.DidNotReceive().AddNewBlock(remoteBlockTree.Head!, Arg.Any<ISyncPeer>());
        remoteServer2.Received().AddNewBlock(Arg.Is<Block>(b => b.Hash == remoteBlockTree.Head!.Hash), Arg.Any<ISyncPeer>());
    }

    [Test]
    public async Task Skip_known_block()
    {
        Context ctx = new();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        ctx.SyncServer = ctx.CreateSyncServer(blockTree);

        ISyncServer remoteServer1 = Substitute.For<ISyncServer>();
        SyncPeerMock syncPeerMock1 = CreateSyncPeerMock(blockTree, remoteServer1, TestItem.PublicKeyA);
        PeerInfo peer1 = new(syncPeerMock1);
        ISyncServer remoteServer2 = Substitute.For<ISyncServer>();
        SyncPeerMock syncPeerMock2 = CreateSyncPeerMock(blockTree, remoteServer2, TestItem.PublicKeyB);
        PeerInfo peer2 = new(syncPeerMock2);
        ConfigurePeers(ctx, [peer1, peer2]);
        Block head = blockTree.Head!;
        ctx.SyncServer.AddNewBlock(head, peer1.SyncPeer);
        await Task.Delay(100); // notifications fire on separate task
        await Task.WhenAll(syncPeerMock1.Close(), syncPeerMock2.Close());
        remoteServer1.DidNotReceive().AddNewBlock(head, Arg.Any<ISyncPeer>());
        remoteServer2.DidNotReceive().AddNewBlock(head, Arg.Any<ISyncPeer>());
        Assert.That(blockTree.FindLevel(head.Number)!.BlockInfos.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task Broadcast_NewBlock_on_arrival_to_sqrt_of_peers([Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 20, 50, 100)] int peerCount)
    {
        int expectedPeers = (int)Math.Ceiling(Math.Sqrt(peerCount - 1));

        Context ctx = new();
        BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree);

        ISyncServer remoteServer = Substitute.For<ISyncServer>();
        int count = 0;
        CountdownEvent received = expectedPeers > 0 ? new CountdownEvent(expectedPeers) : null!;
        remoteServer
            .When(r => r.AddNewBlock(Arg.Is<Block>(b => b.Hash == remoteBlockTree.Head!.Hash), Arg.Any<ISyncPeer>()))
            .Do(_ =>
            {
                int n = Interlocked.Increment(ref count);
                if (received is not null && n <= expectedPeers) received.Signal();
            });
        PeerInfo[] peers = CreatePeerInfos(peerCount, remoteBlockTree, remoteServer);
        ConfigurePeers(ctx, peers);
        ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peers[0].SyncPeer);

        if (received is not null)
        {
            Assert.That(received.Wait(TimeSpan.FromSeconds(30)), Is.True, "Broadcast did not reach all expected peers");
        }
        Assert.That(count, Is.EqualTo(expectedPeers));
        await CloseSyncPeerMocks(peers);
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_when_latest_increased_enough()
    {
        Context ctx = new();

        const int frequency = 32;
        BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(1).TestObject;

        ctx.SyncServer = ctx.CreateSyncServer(localBlockTree);

        PeerInfo[] peers = Enumerable.Range(0, 3)
            .Select(_ => Substitute.For<ISyncPeer>())
            .Select(p => new PeerInfo(p))
            .ToArray();

        const int blocksCount = 100;
        int startBlock = (int)localBlockTree.Head!.Number;

        // Older in-flight range broadcasts are cancelled as the head advances, so intermediate updates
        // may be coalesced away; only the latest range is guaranteed to reach every peer.
        ulong earliestNumber = localBlockTree.GetLowestBlock();
        // AddBranch adds blocks up to branchLength - 1, so the highest head is blocksCount - 1, not blocksCount.
        ulong finalLatest = (ulong)Enumerable.Range(startBlock + 1, blocksCount - 1).Last(x => x % frequency == 0);

        ManualResetEventSlim[] perPeerFinalRange = peers.Select(_ => new ManualResetEventSlim(false)).ToArray();
        for (int i = 0; i < peers.Length; i++)
        {
            int idx = i;
            peers[i].SyncPeer
                .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
                .Do(call =>
                {
                    if (call.ArgAt<BlockHeader>(0).Number == earliestNumber &&
                        call.ArgAt<BlockHeader>(1).Number == finalLatest)
                        perPeerFinalRange[idx].Set();
                });
        }

        ConfigurePeers(ctx, peers);

        localBlockTree.AddBranch(blocksCount / 3, splitBlockNumber: startBlock, splitVariant: 0);
        localBlockTree.AddBranch(blocksCount * 2 / 3, splitBlockNumber: startBlock, splitVariant: 0);
        localBlockTree.AddBranch(blocksCount, splitBlockNumber: startBlock, splitVariant: 0);

        for (int i = 0; i < peers.Length; i++)
        {
            Assert.That(perPeerFinalRange[i].Wait(TimeSpan.FromSeconds(30)), Is.True,
                $"Peer {i} was not notified of the latest block range ({earliestNumber} -> {finalLatest})");
        }
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_with_lowest_stored_block_when_pruner_reports_no_oldest_block()
    {
        Context ctx = new();
        ctx.BlockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        ctx.BlockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        ctx.BlockTree.GetLowestBlock().Returns(100UL);
        ctx.BlockTree.FindHeader(100UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
        ctx.HistoryPruner.OldestBlockHeader.Returns((BlockHeader?)null);

        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        ConfigurePeers(ctx, [peer]);

        using ManualResetEventSlim notified = new(false);
        ulong notifiedEarliest = ulong.MaxValue;
        peer.SyncPeer
            .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                notifiedEarliest = call.ArgAt<BlockHeader>(0).Number;
                notified.Set();
            });

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(128).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notified.Wait(TimeSpan.FromSeconds(30)), Is.True, "Peer was not notified of the block range");
            Assert.That(notifiedEarliest, Is.EqualTo(100UL));
        }
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_floors_at_the_download_pointers_while_the_pruner_defers()
    {
        Context ctx = new();
        ctx.BlockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        ctx.BlockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        ctx.BlockTree.GetLowestBlock().Returns(100UL);
        ctx.BlockTree.FindHeader(120UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(120).TestObject);
        ctx.HistoryPruner.OldestBlockHeader.Returns((BlockHeader?)null);
        ctx.SyncPointers.LowestInsertedBodyNumber.Returns(110UL);
        ctx.SyncPointers.LowestInsertedReceiptBlockNumber.Returns(120UL);

        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        ConfigurePeers(ctx, [peer]);

        using ManualResetEventSlim notified = new(false);
        ulong notifiedEarliest = ulong.MaxValue;
        peer.SyncPeer
            .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                notifiedEarliest = call.ArgAt<BlockHeader>(0).Number;
                notified.Set();
            });

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(128).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notified.Wait(TimeSpan.FromSeconds(30)), Is.True, "Peer was not notified of the block range");
            Assert.That(notifiedEarliest, Is.EqualTo(120UL),
                "while the pruner defers, the advertised earliest must track the later of the body and receipt frontiers, not the config barrier");
            Assert.That(ctx.SyncServer.LowestBlock, Is.EqualTo(120UL),
                "the eth/69 status handshake reads LowestBlock directly, so it must carry the same floor as the range broadcast");
        }
    }

    [TestCase(true, 150UL, 1UL, TestName = "LowestBlock_falls_back_to_the_config_pivot_while_no_ancient_body_was_inserted")]
    [TestCase(false, 150UL, 1UL, TestName = "LowestBlock_ignores_the_pivot_on_a_node_with_no_descending_feed")]
    [TestCase(true, 0UL, 1UL, TestName = "LowestBlock_ignores_the_pivot_on_a_genesis_following_node_without_a_config_pivot")]
    [Parallelizable(ParallelScope.None)]
    public void LowestBlock_floors_at_the_config_pivot_only_under_fast_sync(bool fastSync, ulong configPivot, ulong lowestStored)
    {
        ulong expected = fastSync && configPivot != 0 ? configPivot : lowestStored;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((5_000UL, Keccak.Zero));
        blockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        blockTree.GetLowestBlock().Returns(lowestStored);

        SyncServer syncServer = new(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            blockTree,
            Substitute.For<IBlockStore>(),
            Substitute.For<IHeaderStore>(),
            new HeaderDecoder(),
            NullReceiptStorage.Instance,
            Substitute.For<IBlockAccessListStore>(),
            Always.Valid,
            Always.Valid,
            Substitute.For<ISyncPeerPool>(),
            StaticSelector.Full,
            new TestSyncConfig { FastSync = fastSync, PivotNumber = configPivot },
            Policy.FullGossip,
            Substitute.For<IHistoryPruner>(),
            MainnetSpecProvider.Instance,
            LimboLogs.Instance,
            Substitute.For<ISyncPointers>());

        Assert.That(syncServer.LowestBlock, Is.EqualTo(expected),
            "the floor is the static config pivot, only under fast sync - the live tree pivot rises with finality and would withdraw held history on a genesis-following node");
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_floors_above_a_published_boundary_while_receipts_descend()
    {
        Context ctx = new();
        ctx.BlockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        ctx.BlockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        ctx.BlockTree.GetLowestBlock().Returns(100UL);
        ctx.BlockTree.FindHeader(120UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(120).TestObject);
        ctx.HistoryPruner.OldestBlockHeader.Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
        ctx.SyncPointers.LowestInsertedBodyNumber.Returns(110UL);
        ctx.SyncPointers.LowestInsertedReceiptBlockNumber.Returns(120UL);

        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        ConfigurePeers(ctx, [peer]);

        using ManualResetEventSlim notified = new(false);
        ulong notifiedEarliest = ulong.MaxValue;
        peer.SyncPeer
            .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                notifiedEarliest = call.ArgAt<BlockHeader>(0).Number;
                notified.Set();
            });

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(128).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notified.Wait(TimeSpan.FromSeconds(30)), Is.True, "Peer was not notified of the block range");
            Assert.That(notifiedEarliest, Is.EqualTo(120UL),
                "the broadcast must carry the same floor as the status handshake even when the pruner has already published a lower boundary");
        }
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_floors_the_pruner_published_boundary_too()
    {
        Context ctx = new();
        ctx.BlockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        ctx.BlockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        ctx.BlockTree.GetLowestBlock().Returns(100UL);
        ctx.BlockTree.FindHeader(120UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(120).TestObject);
        ctx.SyncPointers.LowestInsertedBodyNumber.Returns(110UL);
        ctx.SyncPointers.LowestInsertedReceiptBlockNumber.Returns(120UL);

        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        ConfigurePeers(ctx, [peer]);

        using ManualResetEventSlim notified = new(false);
        ulong notifiedEarliest = ulong.MaxValue;
        peer.SyncPeer
            .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                notifiedEarliest = call.ArgAt<BlockHeader>(0).Number;
                notified.Set();
            });

        ctx.HistoryPruner.NewOldestBlock += Raise.EventWith(new OnNewOldestBlockArgs(Build.A.BlockHeader.WithNumber(100).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notified.Wait(TimeSpan.FromSeconds(30)), Is.True, "Peer was not notified of the block range");
            Assert.That(notifiedEarliest, Is.EqualTo(120UL),
                "the pruner published path must carry the same floor as the head driven one, so one peer never sees two different earliest values");
        }
        ctx.BlockTree.Received().UpdateLowestServedBlock(120UL);
    }

    [Test]
    [Parallelizable(ParallelScope.None)]
    public void Broadcast_BlockRangeUpdate_clamps_earliest_to_the_announced_block()
    {
        Context ctx = new();
        ctx.BlockTree.Genesis.Returns(Build.A.BlockHeader.WithNumber(0).TestObject);
        ctx.BlockTree.Head.Returns(Build.A.Block.WithNumber(200).TestObject);
        ctx.BlockTree.GetLowestBlock().Returns(100UL);
        ctx.BlockTree.FindHeader(64UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(64).TestObject);
        ctx.BlockTree.FindHeader(100UL, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(Build.A.BlockHeader.WithNumber(100).TestObject);
        ctx.HistoryPruner.OldestBlockHeader.Returns((BlockHeader?)null);

        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        ConfigurePeers(ctx, [peer]);

        using ManualResetEventSlim notified = new(false);
        ulong notifiedEarliest = ulong.MaxValue;
        peer.SyncPeer
            .When(p => p.NotifyOfNewRange(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                notifiedEarliest = call.ArgAt<BlockHeader>(0).Number;
                notified.Set();
            });

        ctx.BlockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(64).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notified.Wait(TimeSpan.FromSeconds(30)), Is.True, "Peer was not notified of the block range");
            Assert.That(notifiedEarliest, Is.EqualTo(64UL));
        }
    }

    [Test]
    public void Correctly_clips_lowestBlock()
    {
        Context ctx = new();
        ctx.BlockTree.GetLowestBlock().Returns(5UL);
        Assert.That(ctx.SyncServer.LowestBlock, Is.EqualTo(0));
    }

    private static SyncPeerMock CreateSyncPeerMock(
        BlockTree blockTree,
        ISyncServer remoteServer,
        PublicKey remotePublicKey) =>
        new(blockTree, remoteSyncServer: remoteServer, remotePublicKey: remotePublicKey);

    private static PeerInfo[] CreatePeerInfos(int peerCount, BlockTree blockTree, ISyncServer remoteServer)
    {
        PeerInfo[] peers = new PeerInfo[peerCount];
        for (int i = 0; i < peers.Length; i++)
        {
            peers[i] = new PeerInfo(new SyncPeerMock(blockTree, remoteSyncServer: remoteServer));
        }

        return peers;
    }

    private static void ConfigurePeers(Context ctx, PeerInfo[] peers)
    {
        ctx.PeerPool.AllPeers.Returns(peers);
        ctx.PeerPool.PeerCount.Returns(peers.Length);
    }

    private static Task CloseSyncPeerMocks(PeerInfo[] peers)
    {
        Task[] closeTasks = new Task[peers.Length];
        for (int i = 0; i < peers.Length; i++)
        {
            closeTasks[i] = ((SyncPeerMock)peers[i].SyncPeer).Close();
        }

        return Task.WhenAll(closeTasks);
    }

    [TestCaseSource(nameof(BlockAccessListRlpCases))]
    public void GetBlockAccessListRlp_returns_expected_value(
        Hash256? blockAccessListHash,
        byte[]? expectedRlp,
        bool shouldReadStore)
    {
        Context ctx = new();
        IBlockAccessListStore blockAccessListStore = Substitute.For<IBlockAccessListStore>();
        BlockHeader header = BuildBlockAccessListHeader(blockAccessListHash);

        ctx.BlockTree.FindHeader(TestItem.KeccakA, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(header);
        if (shouldReadStore)
        {
            blockAccessListStore.GetRlp(header.Number, TestItem.KeccakA).Returns(ArrayMemoryManager.From(expectedRlp));
        }

        SyncServer syncServer = ctx.CreateSyncServer(blockAccessListStore);

        using MemoryManager<byte>? actualRlp = syncServer.GetBlockAccessListRlp(TestItem.KeccakA);
        if (expectedRlp is null)
        {
            Assert.That(actualRlp, Is.Null);
        }
        else
        {
            Assert.That(actualRlp!.Memory, Is.SequenceEqualTo(expectedRlp));
        }

        if (shouldReadStore)
        {
            blockAccessListStore.Received(1).GetRlp(header.Number, TestItem.KeccakA);
        }
        else
        {
            blockAccessListStore.DidNotReceive().GetRlp(Arg.Any<ulong>(), Arg.Any<Hash256>());
        }
    }

    private static TestCaseData[] BlockAccessListRlpCases =>
    [
        new TestCaseData(null, null, false)
            .SetName("GetBlockAccessListRlp_returns_null_for_pre_eip7928_blocks_without_touching_store"),
        new TestCaseData(TestItem.KeccakB, new byte[] { 0xc1, 0x80 }, true)
            .SetName("GetBlockAccessListRlp_reads_store_for_eip7928_blocks")
    ];

    private static BlockHeader BuildBlockAccessListHeader(Hash256? blockAccessListHash) =>
        Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .WithBlockAccessListHash(blockAccessListHash)
            .TestObject;

    private static void ApplyFakeTotalDifficulty(Block block, bool sendFakeTd)
    {
        if (sendFakeTd)
        {
            block.Header.TotalDifficulty = block.Header.TotalDifficulty * 2;
        }
    }

    [Test]
    public void OnNewRange_is_thread_safe_under_concurrent_head_and_oldest_updates()
    {
        // NewHeadBlock (block-processing thread) and NewOldestBlock (history-pruning background task) both
        // funnel into the private OnNewRange, which swaps the shared range-broadcast CTS. Raising
        // NewOldestBlock from several threads reproduces that concurrency; without synchronisation the swap
        // disposes a source another thread is about to hand to Task.Run, throwing ObjectDisposedException.
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(64).TestObject;
        RaisableHistoryPruner historyPruner = new() { OldestBlockHeader = blockTree.Genesis };

        // Hand-rolled fake rather than a mock: this loop drives ~160k concurrent calls, which would
        // both grow a mock's retained-call log and race its non-thread-safe recording.
        StubSyncPeerPool peerPool = new();

        // Block-scoped using: roots the server and its event subscriptions for the whole concurrent run.
        using (new SyncServer(
            Substitute.For<IWorldStateManager>(),
            new MemDb(),
            blockTree,
            Substitute.For<IBlockStore>(),
            Substitute.For<IHeaderStore>(),
            new HeaderDecoder(),
            NullReceiptStorage.Instance,
            Substitute.For<IBlockAccessListStore>(),
            Always.Valid,
            Always.Valid,
            peerPool,
            StaticSelector.Full,
            new TestSyncConfig(),
            Policy.FullGossip,
            historyPruner,
            MainnetSpecProvider.Instance,
            LimboLogs.Instance))
        {
            BlockHeader oldest = blockTree.Genesis!;
            // The window between publishing a new source and reading its token is a few instructions wide, so the
            // counts are calibrated rather than arbitrary: at 8x20000 the unsynchronised swap fails every run, and
            // the synchronised one completes in under a second.
            const int threadCount = 8;
            const int iterationsPerThread = 20000;
            using ManualResetEventSlim start = new(false);
            ConcurrentQueue<Exception> failures = new();

            // Dedicated threads: eight CPU-bound thread-pool bodies would hold the pool the parallel fixtures share.
            Thread[] racers = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                racers[t] = new Thread(() =>
                {
                    // Rendezvous so every thread is inside the swap window together rather than in sequence.
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < iterationsPerThread; i++)
                        {
                            historyPruner.RaiseNewOldestBlock(oldest);
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Enqueue(e);
                    }
                });
                racers[t].Start();
            }

            start.Set();
            foreach (Thread racer in racers) racer.Join();

            Assert.That(failures, Is.Empty, () => string.Join(Environment.NewLine, failures.Select(static e => e.ToString())));
        }
    }

    /// <param name="start">The number of the canonical start header, or -1 for the side block at <see cref="SideBlockNumber"/>.</param>
    /// <param name="missing">Whether the canonical header at <see cref="MissingHeaderNumber"/> is deleted.</param>
    /// <param name="batched">Whether the server reads main chain requests in batches.</param>
    [Test]
    public void FindHeadersRlp_encodes_the_headers_that_the_block_tree_finds(
        [Values(-1, 0, 5, 19)] int start,
        [Values(0, 1, 4, 30)] int count,
        [Values(0, 2)] int skip,
        [Values] bool reverse,
        [Values] bool missing,
        [Values] bool batched)
    {
        BlockTreeBuilder builder = BuildBlockTree(out CountingChainLevelInfoRepository levels);
        BlockTree tree = builder.TestObject;
        Block sideBlock = Build.A.Block.WithParent(tree.FindBlock(SideBlockNumber - 1, BlockTreeLookupOptions.None)!).WithDifficulty(1).TestObject;
        tree.SuggestBlock(sideBlock);
        if (missing) builder.HeaderStore.Delete(tree.FindHash(MissingHeaderNumber)!);

        Hash256 startHash = start < 0 ? sideBlock.Hash! : tree.FindHash((ulong)start)!;
        using IOwnedReadOnlyList<byte> rlp = FindHeadersRlp(builder, levels, batched, startHash, count, skip, reverse);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rlp.AsSpan().ToArray(), Is.EqualTo(EncodeHeadersTheTreeFinds(tree, startHash, count, skip, reverse)));
            Assert.That(levels.BatchedReads, batched && skip > 0 && count > 0 ? Is.Positive : Is.Zero);
            if (start < 0 && batched && skip > 0 && count > 0) Assert.That(levels.BatchedLevels, Is.EqualTo(1), "levels read in batches from a side chain start");
        }
    }

    /// <remarks>
    /// A forward walk stops at a level without a main chain block, so the batched read answers it alone. A reverse walk
    /// takes the best block of such a level, so the walk answers it.
    /// </remarks>
    [Test]
    public void FindHeadersRlp_with_a_skip_answers_as_the_walk_at_a_level_without_a_main_chain_block([Values] bool reverse)
    {
        BlockTreeBuilder builder = BuildBlockTree(out CountingChainLevelInfoRepository levels);
        BlockTree tree = builder.TestObject;
        ulong holeNumber;
        if (reverse)
        {
            holeNumber = 10;
            ChainLevelInfo level = levels.LoadLevel(holeNumber)!;
            level.HasBlockOnMainChain = false;
            levels.PersistLevel(holeNumber, level);
        }
        else
        {
            holeNumber = tree.BestKnownNumber + 1;
            tree.SuggestBlock(Build.A.Block.WithParent(tree.FindBlock(holeNumber - 1, BlockTreeLookupOptions.None)!).TestObject);
        }

        Hash256 startHash = tree.FindHash(reverse ? holeNumber + 9 : holeNumber - 6)!;
        using IOwnedReadOnlyList<byte> rlp = FindHeadersRlp(builder, levels, batched: true, startHash, 5, 2, reverse);
        int walkLevelReads = levels.LevelReads;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rlp.AsSpan().ToArray(), Is.EqualTo(EncodeHeadersTheTreeFinds(tree, startHash, 5, 2, reverse)));
            Assert.That(levels.BatchedReads, Is.Positive);
            Assert.That(walkLevelReads, reverse ? Is.Positive : Is.Zero, "levels read by the walk");
        }
    }

    /// <remarks>
    /// The reorg replaces the chain above block 8 after the batched read copies the start level and before it reads
    /// the others, so the read finds the start on the old branch and the next headers on the new one.
    /// </remarks>
    [Test]
    public void FindHeadersRlp_with_a_skip_stops_at_a_header_that_a_reorg_during_the_read_moved_off_the_main_chain()
    {
        BlockTreeBuilder builder = BuildBlockTree(out CountingChainLevelInfoRepository levels);
        BlockTree tree = builder.TestObject;
        BlockHeader start = tree.FindHeader(10, BlockTreeLookupOptions.None)!;
        levels.DuringNextBatchedRead = () =>
        {
            Block parent = tree.FindBlock(8, BlockTreeLookupOptions.None)!;
            for (int i = 0; i < 11; i++)
            {
                parent = Build.A.Block.WithParent(parent).WithDifficulty(parent.Difficulty * 2).TestObject;
                tree.SuggestBlock(parent);
            }

            tree.TryUpdateMainChain(parent.Header, wereProcessed: true, forceUpdateHeadBlock: true);
        };

        using IOwnedReadOnlyList<byte> rlp = FindHeadersRlp(builder, levels, batched: true, start.Hash!, 5, 1, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.FindHash(10), Is.Not.EqualTo(start.Hash), "reorg ran");
            Assert.That(rlp.AsSpan().ToArray(), Is.EqualTo(EncodeHeadersTheTreeFinds(tree, start.Hash!, 1, 0, false)));
        }
    }

    private static BlockTreeBuilder BuildBlockTree(out CountingChainLevelInfoRepository levels)
    {
        BlockTreeBuilder builder = Build.A.BlockTree();
        levels = new CountingChainLevelInfoRepository(new ChainLevelInfoRepository(builder.BlockInfoDb));
        return builder.WithChainLevelInfoRepository(levels).OfChainLength(20);
    }

    /// <summary>Calls <see cref="SyncServer.FindHeadersRlp"/> with the header and level caches empty, so that every read goes to the database.</summary>
    private static IOwnedReadOnlyList<byte> FindHeadersRlp(BlockTreeBuilder builder, CountingChainLevelInfoRepository levels, bool batched, Hash256 startHash, int count, int skip, bool reverse)
    {
        SyncServer server = new Context()
            .CreateSyncServer(builder.TestObject, headerStore: builder.HeaderStore, chainLevelInfoRepository: batched ? levels : null);
        ((IClearableCache)builder.HeaderStore).ClearCache();
        levels.ClearCache();
        levels.ResetCounts();
        return server.FindHeadersRlp(startHash, count, skip, reverse);
    }

    private static byte[] EncodeHeadersTheTreeFinds(BlockTree tree, Hash256 startHash, int count, int skip, bool reverse)
    {
        HeaderDecoder decoder = new();
        using IOwnedReadOnlyList<BlockHeader> headers = tree.FindHeaders(startHash, count, skip, reverse);
        List<byte> expected = [];
        foreach (BlockHeader? header in headers)
        {
            if (header is null) break;
            expected.AddRange(decoder.Encode(header));
        }

        return [.. expected];
    }

    /// <summary>Counts the batched main chain reads and the other level reads that reach a repository.</summary>
    private sealed class CountingChainLevelInfoRepository(ChainLevelInfoRepository inner) : IChainLevelInfoRepository
    {
        public int BatchedReads { get; private set; }
        public int BatchedLevels { get; private set; }
        public int LevelReads { get; private set; }

        public void ResetCounts() => BatchedReads = BatchedLevels = LevelReads = 0;
        public void ClearCache() => ((IClearableCache)inner).ClearCache();

        public void Delete(ulong number, BatchWrite? batch = null) => inner.Delete(number, batch);
        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null) => inner.PersistLevel(number, level, batch);
        public BatchWrite StartBatch() => inner.StartBatch();

        public ChainLevelInfo? LoadLevel(ulong number)
        {
            LevelReads++;
            return inner.LoadLevel(number);
        }

        public ChainLevelInfo? LoadLevelWithoutCaching(ulong number)
        {
            LevelReads++;
            return inner.LoadLevelWithoutCaching(number);
        }

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            LevelReads++;
            return inner.MultiLoadLevel(in blockNumbers);
        }

        /// <summary>Runs before the next batched read of more than one level.</summary>
        public Action? DuringNextBatchedRead { get; set; }

        public void GetMainChainBlocksWithoutCaching(ReadOnlyMemory<ulong> blockNumbers, Memory<(ValueHash256 BlockHash, UInt256 TotalDifficulty)> blocks)
        {
            BatchedReads++;
            BatchedLevels += blockNumbers.Length;
            if (blockNumbers.Length > 1 && DuringNextBatchedRead is { } action)
            {
                DuringNextBatchedRead = null;
                action();
            }

            inner.GetMainChainBlocksWithoutCaching(blockNumbers, blocks);
        }
    }

    [Test]
    public void FindHeadersRlp_finds_nothing_for_an_unknown_start()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(3);
        using IOwnedReadOnlyList<byte> rlp = new Context().CreateSyncServer(builder.TestObject, headerStore: builder.HeaderStore)
            .FindHeadersRlp(TestItem.KeccakA, 2, 0, false);

        Assert.That(rlp.Count, Is.Zero);
    }

    private const ulong SideBlockNumber = 6;
    private const ulong MissingHeaderNumber = 8;

    private sealed class RaisableHistoryPruner : IHistoryPruner
    {
        public ulong? CutoffBlockNumber => null;
        public ulong? BalCutoffBlockNumber => null;
        public BlockHeader? OldestBlockHeader { get; set; }
        public ulong OldestUnreclaimedBlockNumber => 0;
        public ulong RetentionEpochs => 0;
        public event EventHandler<OnNewOldestBlockArgs>? NewOldestBlock;
        public void SchedulePruneHistory() { }
        public ulong GetRetentionBlocks(ulong retentionEpochs) => 0;
        public void RaiseNewOldestBlock(BlockHeader oldest) =>
            NewOldestBlock?.Invoke(this, new OnNewOldestBlockArgs(oldest));
    }

    private sealed class StubSyncPeerPool : ISyncPeerPool
    {
        public IEnumerable<PeerInfo> AllPeers => [];
        public IEnumerable<PeerInfo> InitializedPeers => [];
        public int PeerCount => 1;
        public int InitializedPeersCount => 0;
        public int PeerMaxCount => 0;
        public event EventHandler<PeerBlockNotificationEventArgs>? NotifyPeerBlock { add { } remove { } }
        public Task<SyncPeerAllocation> Allocate(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts allocationContexts, int timeoutMilliseconds = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Free(SyncPeerAllocation syncPeerAllocation) => throw new NotSupportedException();
        public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts allocationContexts) => throw new NotSupportedException();
        public void ReportBreachOfProtocol(PeerInfo peerInfo, DisconnectReason disconnectReason, string details) => throw new NotSupportedException();
        public void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts allocationContexts) => throw new NotSupportedException();
        public Task<int?> EstimateRequestLimit(RequestType requestType, IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts contexts, CancellationToken token) => throw new NotSupportedException();
        public void WakeUpAll() => throw new NotSupportedException();
        public void AddPeer(ISyncPeer syncPeer) => throw new NotSupportedException();
        public void RemovePeer(ISyncPeer syncPeer) => throw new NotSupportedException();
        public void SetPeerPriority(PublicKey id) => throw new NotSupportedException();
        public void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 hash) => throw new NotSupportedException();
        public void Start() => throw new NotSupportedException();
        public PeerInfo? GetPeer(Node node) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class Context
    {
        public Context(IGossipPolicy? gossipPolicy = null)
        {
            NodeWhoSentTheBlock = Substitute.For<ISyncPeer>();
            NodeWhoSentTheBlock.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            PeerPool = Substitute.For<ISyncPeerPool>();

            BlockTree = Substitute.For<IBlockTree>();
            BlockStore = Substitute.For<IBlockStore>();
            HeaderStore = Substitute.For<IHeaderStore>();
            WorldStateManager = Substitute.For<IWorldStateManager>();
            HistoryPruner = Substitute.For<IHistoryPruner>();
            SyncPointers = Substitute.For<ISyncPointers>();

            StaticSelector selector = StaticSelector.Full;
            SyncServer = new SyncServer(
                WorldStateManager,
                new MemDb(),
                BlockTree,
                BlockStore,
                HeaderStore,
                new HeaderDecoder(),
                NullReceiptStorage.Instance,
                Substitute.For<IBlockAccessListStore>(),
                Always.Valid,
                Always.Valid,
                PeerPool,
                selector,
                new TestSyncConfig(),
                gossipPolicy ?? Policy.FullGossip,
                HistoryPruner,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance,
                SyncPointers);
        }

        public IBlockTree BlockTree { get; }
        public IHistoryPruner HistoryPruner { get; }
        public ISyncPointers SyncPointers { get; }
        public ISyncPeerPool PeerPool { get; }
        public SyncServer SyncServer { get; set; }
        public ISpecProvider SpecProvider { get; set; } = null!;

        public IBlockTree LocalBlockTree { get; set; } = null!;
        public ISyncPeer NodeWhoSentTheBlock { get; }

        public IBlockStore BlockStore { get; }

        public IHeaderStore HeaderStore { get; }

        public IWorldStateManager WorldStateManager { get; }

        public SyncServer CreateSyncServer(
            IBlockTree localBlockTree,
            IBlockValidator? blockValidator = null,
            ISealValidator? sealValidator = null,
            ISyncModeSelector? syncModeSelector = null,
            ISpecProvider? specProvider = null,
            IReceiptFinder? receiptFinder = null,
            IHeaderStore? headerStore = null,
            IChainLevelInfoRepository? chainLevelInfoRepository = null,
            IBlockStore? blockStore = null) =>

            new(
                WorldStateManager,
                new MemDb(),
                localBlockTree,
                blockStore ?? Substitute.For<IBlockStore>(),
                headerStore ?? Substitute.For<IHeaderStore>(),
                new HeaderDecoder(),
                receiptFinder ?? NullReceiptStorage.Instance,
                Substitute.For<IBlockAccessListStore>(),
                blockValidator ?? Always.Valid,
                sealValidator ?? Always.Valid,
                PeerPool,
                syncModeSelector ?? StaticSelector.Full,
                new TestSyncConfig(),
                Policy.FullGossip,
                HistoryPruner,
                specProvider ?? MainnetSpecProvider.Instance,
                LimboLogs.Instance,
                chainLevelInfoRepository: chainLevelInfoRepository);

        public SyncServer CreateSyncServer(IBlockAccessListStore blockAccessListStore) =>
            new(
                WorldStateManager,
                new MemDb(),
                BlockTree,
                Substitute.For<IBlockStore>(),
                Substitute.For<IHeaderStore>(),
                new HeaderDecoder(),
                NullReceiptStorage.Instance,
                blockAccessListStore,
                Always.Valid,
                Always.Valid,
                PeerPool,
                StaticSelector.Full,
                new TestSyncConfig(),
                Policy.FullGossip,
                HistoryPruner,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);
    }
}
