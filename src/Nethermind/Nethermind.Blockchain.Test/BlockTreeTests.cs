// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class BlockTreeTests
{
    private TestMemDb _blocksInfosDb = null!;
    private TestMemDb _headersDb = null!;
    private TestMemDb _blocksDb = null!;

    [TearDown]
    public void TearDown()
    {
        _blocksDb?.Dispose();
        _headersDb?.Dispose();
    }

    private BlockTree BuildBlockTree()
    {
        _blocksDb = new TestMemDb();
        _headersDb = new TestMemDb();
        _blocksInfosDb = new TestMemDb();
        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .WithHeadersDb(_headersDb)
            .WithBlockInfoDb(_blocksInfosDb)
            .WithoutSettingHead;
        return builder.TestObject;
    }

    private static void AddToMain(BlockTree blockTree, Block block0)
    {
        blockTree.SuggestBlock(block0);
        blockTree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
    }

    private (BlockTree blockTree, Block genesis) BuildBlockTreeWithGenesis(bool forceUpdateHead = false)
    {
        BlockTree blockTree = BuildBlockTree();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: forceUpdateHead, preloadedBlocks: new[] { genesis });
        return (blockTree, genesis);
    }

    private static Block[] BuildAndSuggestChain(BlockTree blockTree, Block parent, int count)
    {
        Block[] chain = new Block[count];
        for (int i = 0; i < count; i++)
        {
            chain[i] = Build.A.Block.WithNumber(parent.Number + 1).WithParent(parent).TestObject;
            blockTree.SuggestBlock(chain[i]);
            parent = chain[i];
        }
        return chain;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_persists_generated_block_access_lists_for_processed_blocks()
    {
        _blocksDb = new TestMemDb();
        _headersDb = new TestMemDb();
        _blocksInfosDb = new TestMemDb();

        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .WithHeadersDb(_headersDb)
            .WithBlockInfoDb(_blocksInfosDb)
            .WithoutSettingHead;
        BlockTree blockTree = builder.TestObject;
        IBlockAccessListStore blockAccessListStore = builder.BlockAccessListStore;

        Block genesis = Build.A.Block.Genesis.TestObject;
        AddToMain(blockTree, genesis);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithParent(genesis)
            .WithTotalDifficulty(1L)
            .TestObject;

        Assert.That(blockTree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
        using MemoryManager<byte>? missingBal = blockAccessListStore.GetRlp(block.Number, block.Hash!);
        Assert.That(missingBal, Is.Null);

        byte[] encodedBal = Rlp.Encode(new ReadOnlyBlockAccessList()).Bytes;
        block.GeneratedBlockAccessList = new GeneratedBlockAccessList();
        block.EncodedBlockAccessList = encodedBal;
        block.Header.BlockAccessListHash = new Hash256(ValueKeccak.Compute(encodedBal).Bytes);

        blockTree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });

        using MemoryManager<byte>? persistedBal = blockAccessListStore.GetRlp(block.Number, block.Hash!);
        Assert.That(persistedBal, Is.Not.Null);
        Assert.That(persistedBal!.Memory.ToArray(), Is.EqualTo(encodedBal));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_genesis_shall_notify()
    {
        bool hasNotified = false;
        BlockTree blockTree = BuildBlockTree();
        blockTree.NewHeadBlock += (_, _) => { hasNotified = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        Block block = Build.A.Block.WithNumber(0).TestObject;
        AddBlockResult result = blockTree.SuggestBlock(block);
        blockTree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });

        AssertSuggestNotifications(result, hasNotified, hasNotifiedNewSuggested);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_genesis_shall_work_even_with_0_difficulty()
    {
        bool hasNotified = false;
        BlockTree blockTree = BuildBlockTree();
        blockTree.NewBestSuggestedBlock += (_, _) => { hasNotified = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        Block block = Build.A.Block.WithNumber(0).WithDifficulty(0).TestObject;
        AddBlockResult result = blockTree.SuggestBlock(block);

        AssertSuggestNotifications(result, hasNotified, hasNotifiedNewSuggested);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Suggesting_genesis_many_times_does_not_cause_any_trouble()
    {
        BlockTree blockTree = BuildBlockTree();
        Block blockA = Build.A.Block.WithNumber(0).TestObject;
        Block blockB = Build.A.Block.WithNumber(0).TestObject;
        Assert.That(blockTree.SuggestBlock(blockA), Is.EqualTo(AddBlockResult.Added));
        Assert.That(blockTree.SuggestBlock(blockB), Is.EqualTo(AddBlockResult.AlreadyKnown));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_notify_on_new_head_block_after_genesis()
    {
        bool hasNotified = false;
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        blockTree.SuggestBlock(block0);
        blockTree.NewHeadBlock += (_, _) => { hasNotified = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        AddBlockResult result = blockTree.SuggestBlock(block1);
        blockTree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });

        AssertSuggestNotifications(result, hasNotified, hasNotifiedNewSuggested);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_notify_new_head_block_once_and_block_added_to_main_multiple_times_when_adding_multiple_blocks_at_once()
    {
        int newHeadBlockNotifications = 0;
        int blockAddedToMainNotifications = 0;

        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(0).WithParent(block2).TestObject;

        // Canonicalize genesis first (as a real node does) so the later walk stops at it instead of moving it.
        blockTree.SuggestBlock(block0);
        blockTree.TryUpdateMainChain(block0.Header, true);
        blockTree.NewHeadBlock += (_, _) => { newHeadBlockNotifications++; };
        blockTree.BlockAddedToMain += (_, _) => { blockAddedToMainNotifications++; };

        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block3);
        blockTree.TryUpdateMainChain(block3.Header, true, preloadedBlocks: new[] { block1, block2, block3 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newHeadBlockNotifications, Is.EqualTo(1), "new head block");
            Assert.That(blockAddedToMainNotifications, Is.EqualTo(3), "block added to main");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_fires_main_chain_events_after_chain_level_repository_batch_flushed()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;

        AddToMain(blockTree, block0);
        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);

        List<bool> blockAddedDbObservations = [];
        bool newHeadDbObserved = false;
        bool onUpdateDbObserved = false;

        // A new ChainLevelInfoRepository instance starts with an empty cache, so HasBlockOnMainChain
        // can only be observed via the underlying IDb. Pre-fix, TryUpdateMainChain held its write batch
        // open across the event invocations, so a fresh repository would miss the new canonical
        // markers. After the fix, the batch is disposed (and therefore flushed) before any of these
        // events fires, so each subscriber observes a fully persisted level.
        blockTree.BlockAddedToMain += (_, e) =>
        {
            ChainLevelInfoRepository freshRepo = new(_blocksInfosDb);
            ChainLevelInfo? level = freshRepo.LoadLevel(e.Block.Number);
            blockAddedDbObservations.Add(level?.HasBlockOnMainChain == true);
        };
        blockTree.NewHeadBlock += (_, e) =>
        {
            ChainLevelInfoRepository freshRepo = new(_blocksInfosDb);
            ChainLevelInfo? level = freshRepo.LoadLevel(e.Block.Number);
            newHeadDbObserved = level?.HasBlockOnMainChain == true;
        };
        blockTree.OnUpdateMainChain += (_, e) =>
        {
            ChainLevelInfoRepository freshRepo = new(_blocksInfosDb);
            ChainLevelInfo? level = freshRepo.LoadLevel(e.Headers[^1].Number);
            onUpdateDbObserved = level?.HasBlockOnMainChain == true;
        };

        blockTree.TryUpdateMainChain(block2.Header, wereProcessed: true, preloadedBlocks: new[] { block1, block2 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockAddedDbObservations, Is.EqualTo(new[] { true, true }));
            Assert.That(newHeadDbObserved, Is.True);
            Assert.That(onUpdateDbObserved, Is.True);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_reorgs_to_header_loading_branch_blocks_from_store_without_preloading()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        AddToMain(blockTree, block0);

        // Branch A becomes canonical first.
        Block a1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block a2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(a1).TestObject;
        foreach (Block block in new[] { a1, a2 })
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, preloadedBlocks: new[] { block });
        }

        // Branch B is only suggested (present in the store, not on the main chain).
        Block b1 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithDifficulty(5).WithParent(b1).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithDifficulty(7).WithParent(b2).TestObject;
        foreach (Block block in new[] { b1, b2, b3 }) blockTree.SuggestBlock(block);

        List<ulong> addedToMain = [];
        blockTree.BlockAddedToMain += (_, e) => addedToMain.Add(e.Block.Number);

        // Reorg to b3 by header only - no preloaded blocks. TryUpdateMainChain must walk the branch and
        // pull each full block from the store itself.
        bool updated = blockTree.TryUpdateMainChain(b3.Header, wereProcessed: true, forceUpdateHeadBlock: true);

        Assert.That(updated, Is.True);
        Assert.That(blockTree.Head!.Hash, Is.EqualTo(b3.Hash));
        Assert.That(blockTree.IsMainChain(b1.Header) && blockTree.IsMainChain(b2.Header) && blockTree.IsMainChain(b3.Header), Is.True, "branch B canonical");
        Assert.That(blockTree.IsMainChain(a1.Header) || blockTree.IsMainChain(a2.Header), Is.False, "branch A no longer canonical");
        Assert.That(addedToMain, Is.EqualTo(new ulong[] { 1, 2, 3 }), "BlockAddedToMain fired for each reorged block in order");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_returns_false_without_mutating_when_a_predecessor_is_missing()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        AddToMain(blockTree, block0);
        Block head1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        blockTree.SuggestBlock(head1);
        blockTree.TryUpdateMainChain(head1.Header, wereProcessed: true, preloadedBlocks: new[] { head1 });

        // A head whose ancestry is not present in the tree cannot be reorged to: the walk back to the main
        // chain hits a missing predecessor and must bail out without mutating anything.
        Block ghostParent = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject; // never added
        Block newHead = Build.A.Block.WithNumber(2).WithDifficulty(5).WithParent(ghostParent).TestObject;

        bool updated = blockTree.TryUpdateMainChain(newHead.Header, wereProcessed: true, forceUpdateHeadBlock: true);

        Assert.That(updated, Is.False);
        Assert.That(blockTree.Head!.Hash, Is.EqualTo(head1.Hash), "head unchanged after a failed reorg");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_notify_on_new_suggested_block_after_genesis()
    {
        bool hasNotified = false;
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        blockTree.SuggestBlock(block0);
        blockTree.NewBestSuggestedBlock += (_, _) => { hasNotified = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        AddBlockResult result = blockTree.SuggestBlock(block1);

        AssertSuggestNotifications(result, hasNotified, hasNotifiedNewSuggested);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_not_notify_but_add_on_lower_difficulty()
    {
        bool hasNotifiedBest = false;
        bool hasNotifiedHead = false;
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        blockTree.NewHeadBlock += (_, _) => { hasNotifiedHead = true; };
        blockTree.NewBestSuggestedBlock += (_, _) => { hasNotifiedBest = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        AddBlockResult result = blockTree.SuggestBlock(block2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hasNotifiedBest, Is.False, "notification best");
            Assert.That(hasNotifiedHead, Is.False, "notification head");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_ignore_orphans()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).TestObject;
        blockTree.SuggestBlock(block0);
        AddBlockResult result = blockTree.SuggestBlock(block2);
        Assert.That(result, Is.EqualTo(AddBlockResult.UnknownParent));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Shall_ignore_known()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        AddBlockResult result = blockTree.SuggestBlock(block1);
        Assert.That(result, Is.EqualTo(AddBlockResult.AlreadyKnown));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cleans_invalid_blocks_before_starting()
    {
        MemDb blockInfosDb = new();
        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithBlockInfoDb(blockInfosDb)
            .WithoutSettingHead;
        IBlockStore blockStore = builder.BlockStore;
        BlockTree tree = builder.TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash!.Bytes);
        BlockTree tree2 = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree2.BestKnownNumber, Is.EqualTo(0L), "best known");
            Assert.That(tree2.Head?.Number, Is.EqualTo(0), "head");
            Assert.That(tree2.BestSuggestedHeader!.Number, Is.EqualTo(0L), "suggested");
            Assert.That(blockStore.Get(block1.Number, block1.Hash!), Is.Null, "block 1");
            Assert.That(blockStore.Get(block2.Number, block2.Hash!), Is.Null, "block 2");
            Assert.That(blockStore.Get(block3.Number, block3.Hash!), Is.Null, "block 3");
            Assert.That(blockInfosDb.Get(1), Is.Null, "level 1");
            Assert.That(blockInfosDb.Get(2), Is.Null, "level 2");
            Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_cleaning_descendants_of_invalid_does_not_touch_other_branches()
    {
        MemDb blockInfosDb = new();
        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithBlockInfoDb(blockInfosDb)
            .WithoutSettingHead;
        IBlockStore blockStore = builder.BlockStore;
        BlockTree tree = builder.TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;
        Block block2B = Build.A.Block.WithNumber(2).WithDifficulty(1).WithParent(block1B).TestObject;
        Block block3B = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2B).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.SuggestBlock(block1B);
        tree.SuggestBlock(block2B);
        tree.SuggestBlock(block3B);

        blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash!.Bytes);
        BlockTree tree2 = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree2.BestKnownNumber, Is.EqualTo(3L), "best known");
            Assert.That(tree2.Head?.Number, Is.EqualTo(0), "head");
            Assert.That(tree2.BestSuggestedHeader!.Hash, Is.EqualTo(block3B.Hash), "suggested");

            Assert.That(blockStore.Get(block1.Number, block1.Hash!), Is.Null, "block 1");
            Assert.That(blockStore.Get(block2.Number, block2.Hash!), Is.Null, "block 2");
            Assert.That(blockStore.Get(block3.Number, block3.Hash!), Is.Null, "block 3");

            Assert.That(blockInfosDb.Get(1), Is.Not.Null, "level 1");
            Assert.That(blockInfosDb.Get(2), Is.Not.Null, "level 2");
            Assert.That(blockInfosDb.Get(3), Is.Not.Null, "level 3");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_load_best_known_up_to_256million()
    {
        _blocksDb = new TestMemDb();
        _headersDb = new TestMemDb();
        TestMemDb blocksInfosDb = new();

        Rlp chainLevel = Rlp.Encode(new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
        blocksInfosDb.ReadFunc = (key) =>
        {
            if (!Bytes.AreEqual(key, BlockTree.DeletePointerAddressInDb.Bytes))
            {
                return chainLevel.Bytes;
            }

            return null!;
        };

        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlocksDb(_blocksDb)
            .WithHeadersDb(_headersDb)
            .WithBlockInfoDb(blocksInfosDb)
            .TestObject;

        Assert.That(blockTree.BestKnownNumber, Is.EqualTo(256000000));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_and_find_branch()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block = Build.A.Block.TestObject;
        blockTree.SuggestBlock(block);
        Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.None);
        Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_on_branch_move_find()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block = Build.A.Block.TestObject;
        AddToMain(blockTree, block);
        Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
        Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_on_branch_move_find_via_block_finder_interface()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block = Build.A.Block.TestObject;
        AddToMain(blockTree, block);
        Block? found = ((IBlockFinder)blockTree).FindBlock(new BlockParameter(block.Hash!, true));
        Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_on_branch_and_not_find_on_main()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block = Build.A.Block.TestObject;
        blockTree.SuggestBlock(block);
        Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
        Assert.That(found, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Add_on_branch_and_not_find_on_main_via_block_finder_interface()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block = Build.A.Block.TestObject;
        blockTree.SuggestBlock(block);
        Block? found = ((IBlockFinder)blockTree).FindBlock(new BlockParameter(block.Hash!, true));
        Assert.That(found, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_header_with_require_canonical_returns_null_when_chain_level_is_missing()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(1);
        BlockTree blockTree = builder.TestObject;

        BlockHeader headerWithoutLevel = Build.A.BlockHeader.WithNumber(2).WithTotalDifficulty(3_000_000).TestObject;
        Assert.That(blockTree.Insert(headerWithoutLevel, BlockTreeInsertHeaderOptions.BeaconHeaderMetadata), Is.EqualTo(AddBlockResult.Added));
        builder.ChainLevelInfoRepository.Delete(headerWithoutLevel.Number);

        Assert.That(blockTree.BestKnownBeaconNumber, Is.GreaterThan(blockTree.BestKnownNumber),
            "test setup must take the path where the level-creation guard skips creating the missing level");
        Assert.That(blockTree.FindHeader(headerWithoutLevel.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_block_with_require_canonical_returns_null_when_chain_level_is_missing()
    {
        // Regression test for issue #8029: an unclean shutdown between the block write and the chain level
        // write leaves a block without a level. When the beacon search guard skips level creation,
        // FindBlock with RequireCanonical used to throw NullReferenceException on the missing level.
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(1);
        BlockTree blockTree = builder.TestObject;

        Block blockWithoutLevel = Build.A.Block.WithNumber(2).WithTotalDifficulty(3_000_000L).TestObject;
        Assert.That(blockTree.Insert(blockWithoutLevel, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconHeaderMetadata), Is.EqualTo(AddBlockResult.Added));
        builder.ChainLevelInfoRepository.Delete(blockWithoutLevel.Number);

        Assert.That(blockTree.BestKnownBeaconNumber, Is.GreaterThan(blockTree.BestKnownNumber),
            "test setup must take the path where the level-creation guard skips creating the missing level");
        Assert.That(blockTree.FindBlock(blockWithoutLevel.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_by_number_basic()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        Block? found = blockTree.FindBlock(2, BlockTreeLookupOptions.None);
        Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block2.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_by_number_beyond_what_is_known_returns_null()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        Block? found = blockTree.FindBlock(1920000, BlockTreeLookupOptions.None);
        Assert.That(found, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_by_number_returns_null_when_block_is_missing()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);

        Block? found = blockTree.FindBlock(5, BlockTreeLookupOptions.None);
        Assert.That(found, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_headers_basic()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block0.Hash, 2, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_headers_skip()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block0.Hash, 2, 1, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block2.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_headers_reverse()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithParent(block2).TestObject;
        Block block4 = Build.A.Block.WithNumber(4).WithParent(block3).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);
        AddToMain(blockTree, block3);
        AddToMain(blockTree, block4);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block2.Hash, 2, 0, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_headers_reverse_skip()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block2.Hash, 2, 1, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block0.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_headers_reverse_below_zero()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block0.Hash, 2, 1, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[1], Is.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_finding_headers_does_not_find_a_header_it_breaks_the_loop()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block0.Hash, 100, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(100));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[3], Is.Null);

            Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_finding_blocks_does_not_find_a_block_it_breaks_the_loop()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> headers = blockTree.FindHeaders(block0.Hash, 100, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Count, Is.EqualTo(100));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[3], Is.Null);

            Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_basic_longer()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        int length = 256;
        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block0.Hash, length, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Count, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_basic_shorter()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        int length = 2;
        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block1.Hash, length, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Count, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_basic()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        int length = 3;
        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block0.Hash, length, 0, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Count, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_reverse()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block2.Hash, 3, 0, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Count, Is.EqualTo(3));

            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block2.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block0.Hash));
        }
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_zero_blocks()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block0.Hash, 0, 0, false);
        Assert.That(blocks.Count, Is.EqualTo(0));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_one_block()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block2.Hash, 1, 0, false);
        Assert.That(blocks.Count, Is.EqualTo(1));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_basic_skip()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block0.Hash, 2, 1, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Count, Is.EqualTo(2), "length");
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Find_sequence_some_empty()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        AddToMain(blockTree, block2);

        using IOwnedReadOnlyList<BlockHeader> blocks = blockTree.FindHeaders(block0.Hash, 4, 0, false);
        Assert.That(blocks.Count, Is.EqualTo(4));
        Assert.That(blocks[3], Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Total_difficulty_is_calculated_when_exists_parent_with_total_difficulty()
    {
        BlockTree blockTree = BuildBlockTree();

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        blockTree.SuggestBlock(block0);
        Block block1 = Build.A.Block.WithNumber(1).WithParentHash(block0.Hash!).WithDifficulty(2).TestObject;
        blockTree.SuggestBlock(block1);
        Assert.That(block1.TotalDifficulty, Is.Not.Null);
        Assert.That((int)block1.TotalDifficulty!, Is.EqualTo(3));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Total_difficulty_is_null_when_no_parent()
    {
        BlockTree blockTree = BuildBlockTree();

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        blockTree.SuggestBlock(block0);

        Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParentHash(Keccak.Zero).TestObject;
        blockTree.SuggestBlock(block2);
        Assert.That(block2.TotalDifficulty, Is.EqualTo(null));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Head_block_gets_updated()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);

        Assert.That(blockTree.Head!.CalculateHash(), Is.EqualTo(block1.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Best_suggested_block_gets_updated()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);
        blockTree.SuggestBlock(block1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.Head!.CalculateHash(), Is.EqualTo(block0.Hash), "head block");
            Assert.That(blockTree.BestSuggestedHeader!.CalculateHash(), Is.EqualTo(block1.Hash), "best suggested");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Sets_genesis_block()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        AddToMain(blockTree, block0);

        Assert.That(blockTree.Genesis!.CalculateHash(), Is.EqualTo(block0.Hash));
    }

    // safeBlockHash is null in the AuRa-finalization-post-snap case (#11775): SafeHash has not been
    // set yet, so ForkChoiceUpdated must tolerate a null safe (and finalized) hash without NRE'ing in
    // HeaderStore.GetBlockNumber. The subscriber forces evaluation of the OnForkChoiceUpdated args
    // (the GetBlockNumber lookups), which is skipped when the event has no subscribers.
    [TestCase(true, TestName = "ForkChoiceUpdated_update_hashes")]
    [TestCase(false, TestName = "ForkChoiceUpdated_tolerates_null_safe_hash")]
    public void ForkChoiceUpdated_update_hashes(bool withSafeHash)
    {
        BlockTree blockTree = BuildBlockTree();
        blockTree.OnForkChoiceUpdated += (_, _) => { };

        Hash256 finalizedBlockHash = TestItem.KeccakB;
        Hash256? safeBlockHash = withSafeHash ? TestItem.KeccakC : null;
        blockTree.ForkChoiceUpdated(finalizedBlockHash, safeBlockHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FinalizedHash, Is.EqualTo(finalizedBlockHash));
            Assert.That(blockTree.SafeHash, Is.EqualTo(safeBlockHash));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Stores_multiple_blocks_per_level()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);
        blockTree.SuggestBlock(block1B);

        Block? found = blockTree.FindBlock(block1B.Hash, BlockTreeLookupOptions.None);

        Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block1B.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_init_head_block_from_db_by_hash()
    {
        Block genesisBlock = Build.A.Block.Genesis.TestObject;
        Block headBlock = genesisBlock;

        BlockStore blockStore = new(new MemDb());
        blockStore.Insert(genesisBlock);

        TestMemDb headersDb = new();
        headersDb.Set(genesisBlock.Hash!, Rlp.Encode(genesisBlock.Header).Bytes);

        TestMemDb blockInfosDb = new();
        blockInfosDb.Set(Keccak.Zero, genesisBlock.Hash!.Bytes);
        ChainLevelInfo level = new(true, new BlockInfo(headBlock.Hash!, headBlock.Difficulty));
        level.BlockInfos[0].WasProcessed = true;

        blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockStore(blockStore)
            .WithHeadersDb(headersDb)
            .WithBlockInfoDb(blockInfosDb)
            .WithSpecProvider(OlympicSpecProvider.Instance)
            .TestObject;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.Head?.Hash, Is.EqualTo(headBlock.Hash), "head");
            Assert.That(blockTree.Genesis?.Hash, Is.EqualTo(headBlock.Hash), "genesis");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Sets_head_block_hash_in_db_on_new_head_block()
    {
        TestMemDb blockInfosDb = new();

        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        AddToMain(blockTree, block0);
        AddToMain(blockTree, block1);

        Hash256 dec = new(blockInfosDb.Get(Keccak.Zero)!);
        Assert.That(dec, Is.EqualTo(block1.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_check_if_block_was_processed()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        Assert.That(blockTree.WasProcessed(block1.Number, block1.Hash!), Is.False, "before");
        blockTree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block0, block1 });
        Assert.That(blockTree.WasProcessed(block1.Number, block1.Hash!), Is.True, "after");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Best_known_number_is_set()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        Assert.That(blockTree.BestKnownNumber, Is.EqualTo(1L));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Is_main_chain_returns_false_when_on_branch()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        Assert.That(blockTree.IsMainChain(block1.Hash!), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Is_main_chain_returns_true_when_on_main()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        blockTree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        Assert.That(blockTree.IsMainChain(block1.Hash!), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Pending_returns_head()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0);
        blockTree.SuggestBlock(block1);
        blockTree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.BestSuggestedHeader, Is.EqualTo(block1.Header));
            Assert.That(blockTree.PendingHash, Is.EqualTo(block0.Hash!));
            Block? pending = ((IBlockFinder)blockTree).FindPendingBlock();
            Assert.That(pending!.Header, Is.SameAs(block0.Header));
            Assert.That(pending.Body, Is.EqualTo(block0.Body));
            Assert.That(((IBlockFinder)blockTree).FindPendingHeader(), Is.SameAs(block0.Header));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Is_main_chain_returns_true_on_fast_sync_block()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0, BlockTreeSuggestOptions.None);
        Assert.That(blockTree.IsMainChain(block0.Hash!), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Was_processed_returns_true_on_fast_sync_block()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0, BlockTreeSuggestOptions.None);
    }

    [Test(Description = "There was a bug where we switched positions and used the index from before the positions were switched"), MaxTime(Timeout.MaxTestTime)]
    public void When_moving_to_main_one_of_the_two_blocks_at_given_level_the_was_processed_check_is_executed_on_the_correct_block_index_regression()
    {
        TestMemDb blockInfosDb = new();

        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .TestObject;
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;

        AddToMain(blockTree, block0);

        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block1);
        blockTree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });

        Hash256 storedInDb = new(blockInfosDb.Get(Keccak.Zero)!);
        Assert.That(storedInDb, Is.EqualTo(block1.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_deleting_invalid_block_sets_head_bestKnown_and_suggested_right()
    {
        BlockTree tree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        tree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        tree.DeleteInvalidBlock(block2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(block1.Number));
            Assert.That(tree.Head?.Header, Is.EqualTo(block1.Header));
            Assert.That(tree.BestSuggestedHeader, Is.EqualTo(block1.Header));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_deleting_invalid_block_deletes_its_descendants()
    {
        BlockStore blockStore = new(new MemDb());
        MemDb blockInfosDb = new();
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .WithBlockStore(blockStore)
            .TestObject;
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        tree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        tree.DeleteInvalidBlock(block2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(1L), "best known");
            Assert.That(tree.Head!.Number, Is.EqualTo(1L), "head");
            Assert.That(tree.BestSuggestedHeader!.Number, Is.EqualTo(1L), "suggested");

            Assert.That(blockStore.Get(block1.Number, block1.Hash!), Is.Not.Null, "block 1");
            Assert.That(blockStore.Get(block2.Number, block2.Hash!), Is.Null, "block 2");
            Assert.That(blockStore.Get(block3.Number, block3.Hash!), Is.Null, "block 3");

            Assert.That(blockInfosDb.Get(1), Is.Not.Null, "level 1");
            Assert.That(blockInfosDb.Get(2), Is.Null, "level 2");
            Assert.That(blockInfosDb.Get(3), Is.Null, "level 3");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_deleting_invalid_block_deletes_its_descendants_even_if_not_first()
    {
        BlockStore blockStore = new(new MemDb());
        MemDb blockInfosDb = new();
        ChainLevelInfoRepository repository = new(blockInfosDb);

        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBlockInfoDb(blockInfosDb)
            .WithBlockStore(blockStore)
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        Block block1b = Build.A.Block.WithNumber(1).WithDifficulty(2).WithExtraData(new byte[] { 1 }).WithParent(block0).TestObject;
        Block block2b = Build.A.Block.WithNumber(2).WithDifficulty(3).WithExtraData(new byte[] { 1 }).WithParent(block1b).TestObject;
        Block block3b = Build.A.Block.WithNumber(3).WithDifficulty(4).WithExtraData(new byte[] { 1 }).WithParent(block2b).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.SuggestBlock(block1b);
        tree.SuggestBlock(block2b);
        tree.SuggestBlock(block3b);

        tree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        tree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        tree.DeleteInvalidBlock(block1b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(3L), "best known");
            Assert.That(tree.Head!.Number, Is.EqualTo(1L), "head");
            Assert.That(tree.BestSuggestedHeader!.Number, Is.EqualTo(1L), "suggested");

            Assert.That(blockStore.Get(block1.Number, block1.Hash!), Is.Not.Null, "block 1");
            Assert.That(blockStore.Get(block2.Number, block2.Hash!), Is.Not.Null, "block 2");
            Assert.That(blockStore.Get(block3.Number, block3.Hash!), Is.Not.Null, "block 3");
            Assert.That(blockStore.Get(block1b.Number, block1b.Hash!), Is.Null, "block 1b");
            Assert.That(blockStore.Get(block2b.Number, block2b.Hash!), Is.Null, "block 2b");
            Assert.That(blockStore.Get(block3b.Number, block3b.Hash!), Is.Null, "block 3b");

            Assert.That(blockInfosDb.Get(1), Is.Not.Null, "level 1");
            Assert.That(blockInfosDb.Get(2), Is.Not.Null, "level 2");
            Assert.That(blockInfosDb.Get(3), Is.Not.Null, "level 3");

            Assert.That(repository.LoadLevel(1)!.BlockInfos.Length, Is.EqualTo(1));
            Assert.That(repository.LoadLevel(2)!.BlockInfos.Length, Is.EqualTo(1));
            Assert.That(repository.LoadLevel(3)!.BlockInfos.Length, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void After_removing_invalid_block_will_not_accept_it_again()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.DeleteInvalidBlock(block1);
        AddBlockResult result = tree.SuggestBlock(block1);
        Assert.That(result, Is.EqualTo(AddBlockResult.InvalidBlock));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void After_deleting_invalid_block_will_accept_other_blocks()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

        Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);

        tree.DeleteInvalidBlock(block1);
        AddBlockResult result = tree.SuggestBlock(block1B);
        Assert.That(result, Is.EqualTo(AddBlockResult.Added));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_deleting_invalid_block_does_not_delete_blocks_that_are_not_its_descendants()
    {
        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
        Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
        Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

        Block block3bad = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2).TestObject;

        tree.SuggestBlock(block0);
        tree.SuggestBlock(block1);
        tree.SuggestBlock(block2);
        tree.SuggestBlock(block3);
        tree.SuggestBlock(block4);
        tree.SuggestBlock(block5);

        tree.SuggestBlock(block3bad);

        tree.TryUpdateMainChain(block5.Header, true, preloadedBlocks: new[] { block5 });
        tree.DeleteInvalidBlock(block3bad);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "best known");
            Assert.That(tree.Head?.Header, Is.EqualTo(block5.Header), "head");
            Assert.That(tree.BestSuggestedHeader!.Hash, Is.EqualTo(block5.Hash), "suggested");
        }
    }

    [Test]
    public void Report_bad_block_stores_block_and_does_not_alter_main_chain()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(3);
        BlockTree blockTree = builder.TestObject;
        BlockHeader originalSuggested = blockTree.BestSuggestedHeader!;
        Block bad = Build.A.Block.WithNumber(4).WithParent(blockTree.Head!).TestObject;

        blockTree.ReportBadBlock(bad);

        Block[] stored = builder.BadBlockStore.GetAll().ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Has.Length.EqualTo(1));
            Assert.That(stored[0].Hash, Is.EqualTo(bad.Hash!));
            Assert.That(blockTree.FindBlock(bad.Hash!, BlockTreeLookupOptions.AllowInvalid), Is.Not.Null);
            Assert.That(blockTree.BestSuggestedHeader, Is.EqualTo(originalSuggested),
                "ReportBadBlock must not roll back BestSuggested the way DeleteInvalidBlock does");
        }
    }

    [Test]
    public void Report_bad_block_ignores_block_without_hash()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(3);
        BlockTree blockTree = builder.TestObject;
        Block badNoHash = new(new BlockHeader(), new BlockBody());

        blockTree.ReportBadBlock(badNoHash);

        Assert.That(builder.BadBlockStore.GetAll(), Is.Empty);
    }

    [Test, MaxTime(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
    public void When_lowestInsertedHeaderWasNotPersisted_useBinarySearchToLoadLowestInsertedHeader(ulong beginIndex, ulong insertedBlocks)
    {
        ulong? expectedResult = insertedBlocks == 0ul ? null : beginIndex - insertedBlocks + 1ul;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = beginIndex,
        };

        BlockTreeBuilder builder = Build.A
            .BlockTree()
            .WithSyncConfig(syncConfig)
            .WithoutSettingHead;
        BlockTree tree = builder.TestObject;
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        for (ulong k = 0; k < insertedBlocks; k++)
        {
            ulong i = beginIndex - k;
            tree.Insert(Build.A.BlockHeader.WithNumber(i).WithTotalDifficulty(i).TestObject);
        }

        builder.MetadataDb.Delete(MetadataDbKeys.LowestInsertedFastHeaderHash);

        tree = Build.A.BlockTree()
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.LowestInsertedHeader?.Number, Is.EqualTo(expectedResult), "tree");
    }

    [Test]
    public void When_lowestInsertedHeaderWasPersisted_doNot_useBinarySearchToLoadLowestInsertedHeader()
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = 105,
        };

        BlockTreeBuilder builder = Build.A
            .BlockTree()
            .WithSyncConfig(syncConfig);
        BlockTree tree = builder.TestObject;
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);
        tree.RecalculateTreeLevels();

        for (ulong i = 1ul; i < 100ul; i++)
        {
            tree.Insert(Build.A.BlockHeader.WithNumber(i).WithParent(tree.FindHeader(i - 1ul, BlockTreeLookupOptions.None)!).TestObject);
        }

        BlockTree loadedTree = Build.A.BlockTree()
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.LowestInsertedHeader?.Number, Is.EqualTo(null));
        Assert.That(loadedTree.LowestInsertedHeader?.Number, Is.EqualTo(null));

        loadedTree.LowestInsertedHeader = tree.FindHeader(50, BlockTreeLookupOptions.None);

        loadedTree = Build.A.BlockTree()
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(loadedTree.LowestInsertedHeader?.Number, Is.EqualTo(50));
    }

    [TestCase(5ul, 10ul)]
    [TestCase(10ul, 10ul)]
    [TestCase(12ul, 0ul)]
    public void Does_not_load_bestKnownNumber_before_syncPivot(ulong syncPivot, ulong expectedBestKnownNumber)
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = syncPivot
        };

        MemDb blockInfosDb = new();
        MemDb headersDb = new();
        MemDb blockDb = new();

        _ = Build.A.BlockTree()
            .WithHeadersDb(headersDb)
            .WithBlockInfoDb(blockInfosDb)
            .WithBlocksDb(blockDb)
            .OfChainLength(11)
            .TestObject;

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .WithHeadersDb(headersDb)
            .WithBlockInfoDb(blockInfosDb)
            .WithBlocksDb(blockDb)
            .TestObject;

        Assert.That(tree.BestKnownNumber, Is.EqualTo(expectedBestKnownNumber));
    }

    private static readonly object[] SourceOfBSearchTestCases =
    {
        new object[] {1ul, 0ul},
        new object[] {1ul, 1ul},
        new object[] {2ul, 0ul},
        new object[] {2ul, 1ul},
        new object[] {2ul, 2ul},
        new object[] {3ul, 0ul},
        new object[] {3ul, 1ul},
        new object[] {3ul, 2ul},
        new object[] {3ul, 3ul},
        new object[] {4ul, 0ul},
        new object[] {4ul, 1ul},
        new object[] {4ul, 2ul},
        new object[] {4ul, 3ul},
        new object[] {4ul, 4ul},
        new object[] {5ul, 0ul},
        new object[] {5ul, 1ul},
        new object[] {5ul, 2ul},
        new object[] {5ul, 3ul},
        new object[] {5ul, 4ul},
        new object[] {5ul, 5ul},
        new object[] {728000ul, 0ul},
        new object[] {7280000ul, 1ul}
    };

    [Test, MaxTime(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
    public void Loads_best_known_correctly_on_inserts(ulong beginIndex, ulong insertedBlocks)
    {
        ulong expectedResult = insertedBlocks == 0ul ? 0ul : beginIndex;

        SyncConfig syncConfig = new()
        {
            PivotNumber = beginIndex,
            FastSync = true,
        };

        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSyncConfig(syncConfig);

        BlockTree tree = builder.TestObject;

        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        for (ulong k = 0; k < insertedBlocks; k++)
        {
            ulong i = beginIndex - k;
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
            tree.Insert(block.Header);
            tree.Insert(block);
        }

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(expectedResult), "tree");
            Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(expectedResult), "loaded tree");
        }
    }

    [Test]
    public void Loads_best_head_up_to_best_persisted_state()
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber, Rlp.Encode(51).Bytes);

        SyncConfig syncConfig = new()
        {
            PivotNumber = 0,
            FastSync = true,
        };

        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithoutSettingHead
            .WithMetadataDb(metadataDb)
            .WithSyncConfig(syncConfig);

        BlockTree tree = builder.TestObject;
        Block genesis = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(genesis);
        Block parent = genesis;

        List<Block> blocks = [genesis];

        for (ulong i = 1ul; i < 100ul; i++)
        {
            Block block = Build.A.Block
                .WithNumber(i)
                .WithParent(parent)
                .WithTotalDifficulty(i).TestObject;
            blocks.Add(block);
            parent = block;
            if (i <= 50ul)
            {
                // tree.Insert(block.Header);
                tree.SuggestBlock(block);
            }
            else
            {
                tree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconBodyMetadata);
            }
        }
        // Blocks above 50 are beacon-inserted (already on the beacon main chain), so a single walk from the
        // tip short-circuits at the first beacon parent. Move exactly the supplied blocks so the whole
        // pre-state is canonical, then assert the reload caps the head at the best persisted state.
        tree.ForceMainChainForTest(blocks);
        tree.BestPersistedState = 50ul;

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(loadedTree.Head?.Number, Is.EqualTo(50ul));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(1ul)]
    [TestCase(2ul)]
    [TestCase(3ul)]
    public void Loads_best_known_correctly_on_inserts_followed_by_suggests(ulong pivotNumber)
    {
        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber,
        };
        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSyncConfig(syncConfig);

        BlockTree tree = builder.TestObject;
        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        Block? pivotBlock = null;
        for (ulong i = pivotNumber; i > 0; i--)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
            pivotBlock ??= block;
            tree.Insert(block.Header);
        }

        tree.SuggestHeader(Build.A.BlockHeader.WithNumber(pivotNumber + 1ul).WithParent(pivotBlock!.Header).TestObject);

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.BestKnownNumber, Is.EqualTo(pivotNumber + 1ul), "tree");
            Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(pivotNumber + 1ul), "loaded tree");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Loads_best_known_correctly_when_head_before_pivot()
    {
        ulong pivotNumber = 1000ul;
        ulong head = 10ul;
        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber
        };

        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(head + 1);

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(treeBuilder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(head), "loaded tree");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_insert_genesis()
    {
        ulong pivotNumber = 0ul;

        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber,
        };

        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSyncConfig(syncConfig)
            .TestObject;

        Block genesis = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(genesis);
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis));
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis.Header));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_set_zero_total_difficulty()
    {
        ulong pivotNumber = 0ul;

        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber,
        };

        CustomSpecProvider specProvider = new(((ForkActivation)0, London.Instance));
        specProvider.UpdateMergeTransitionInfo(null, 0);

        BlockTree tree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSyncConfig(syncConfig)
            .WithSpecProvider(specProvider)
            .TestObject;

        Block genesis = Build.A.Block.WithDifficulty(0).TestObject;
        Assert.That(tree.SuggestBlock(genesis), Is.EqualTo(AddBlockResult.Added));
        Assert.That(tree.FindBlock(genesis.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty, Is.EqualTo(UInt256.Zero));

        Block A = Build.A.Block.WithParent(genesis).WithDifficulty(0).TestObject;
        Assert.That(tree.SuggestBlock(A), Is.EqualTo(AddBlockResult.Added));
        Assert.That(tree.FindBlock(A.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty, Is.EqualTo(UInt256.Zero));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Persists_chain_level_info()
    {
        ulong pivotNumber = 5ul;

        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber,
        };

        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();

        BlockTree tree = Build.A.BlockTree()
            .WithChainLevelInfoRepository(chainLevelInfoRepository)
            .WithSyncConfig(syncConfig)
            .TestObject;

        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        for (ulong i = 5ul; i > 0; i--)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(1ul).TestObject;
            tree.Insert(block.Header);
            Received.InOrder(() =>
            {
                chainLevelInfoRepository.PersistLevel(block.Header.Number, Arg.Any<ChainLevelInfo>(), Arg.Any<BatchWrite>());
            });
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Block_loading_is_lazy()
    {
        SyncConfig syncConfig = new()
        {
            PivotNumber = 0ul,
        };

        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithSyncConfig(syncConfig);
        BlockTree tree = builder
            .TestObject;

        Block genesis = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(genesis);

        Block previousBlock = genesis;
        for (ulong i = 1ul; i < 10ul; i++)
        {
            Block block = Build.A.Block.WithNumber(i).WithParent(previousBlock).TestObject;
            tree.SuggestBlock(block);
            previousBlock = block;
        }

        Block lastBlock = previousBlock;

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSyncConfig(syncConfig)
            .WithDatabaseFrom(builder)
            .TestObject;
        loadedTree.FindHeader(lastBlock.Hash, BlockTreeLookupOptions.None);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_find_genesis_level()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ChainLevelInfo info = blockTree.FindLevel(0)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.HasBlockOnMainChain, Is.True);
            Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_find_some_level()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ChainLevelInfo info = blockTree.FindLevel(1)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.HasBlockOnMainChain, Is.True);
            Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_find_future_level()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ChainLevelInfo info = blockTree.FindLevel(1000)!;
        Assert.That(info, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_delete_a_future_slice()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(1000, 2000);
        Assert.That(blockTree.Head!.Number, Is.EqualTo(2));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_delete_slice()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(2, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FindBlock(2, BlockTreeLookupOptions.None), Is.Null);
            Assert.That(blockTree.FindHeader(2, BlockTreeLookupOptions.None), Is.Null);
            Assert.That(blockTree.FindLevel(2), Is.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Does_not_delete_outside_of_the_slice()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(2, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(blockTree.FindHeader(1, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(blockTree.FindLevel(1), Is.Not.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_delete_one_block()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(2, 2);
        Assert.That(blockTree.Head!.Number, Is.EqualTo(1));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_delete_two_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(1, 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FindLevel(1), Is.Null);
            Assert.That(blockTree.FindLevel(2), Is.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_delete_in_the_middle()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(1, 1);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Throws_when_start_after_end()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(2, 1));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Throws_when_start_at_zero()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(0, 1));
    }



    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_delete_too_many()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(1000, 52001));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_add_blocks_when_blocked()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.BlockAcceptingNewBlocks();
        Assert.That(blockTree.SuggestBlock(Build.A.Block.WithNumber(3).TestObject), Is.EqualTo(AddBlockResult.CannotAccept));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_block_cannot_insert_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.That(blockTree.CanAcceptNewBlocks, Is.True);
        blockTree.BlockAcceptingNewBlocks();
        Assert.That(blockTree.CanAcceptNewBlocks, Is.False);
        Block newBlock = Build.A.Block.WithNumber(3).TestObject;
        AddBlockResult result = blockTree.Insert(newBlock);
        Assert.That(result, Is.EqualTo(AddBlockResult.CannotAccept));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_skip_blocked_tree()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.That(blockTree.CanAcceptNewBlocks, Is.True);
        blockTree.BlockAcceptingNewBlocks();
        Assert.That(blockTree.CanAcceptNewBlocks, Is.False);
        Block newBlock = Build.A.Block.WithNumber(3).TestObject;
        AddBlockResult result = blockTree.Insert(newBlock, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks);
        Assert.That(result, Is.EqualTo(AddBlockResult.Added));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_block_and_unblock_adding_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.That(blockTree.CanAcceptNewBlocks, Is.True);
        blockTree.BlockAcceptingNewBlocks();
        Assert.That(blockTree.CanAcceptNewBlocks, Is.False);
        blockTree.BlockAcceptingNewBlocks();
        blockTree.ReleaseAcceptingNewBlocks();
        Assert.That(blockTree.CanAcceptNewBlocks, Is.False);
        blockTree.ReleaseAcceptingNewBlocks();
        Assert.That(blockTree.CanAcceptNewBlocks, Is.True);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(10ul, false, 10000000ul)]
    [TestCase(4ul, false, 4000000ul)]
    [TestCase(10ul, true, 10000000ul)]
    public void Recovers_total_difficulty(ulong chainLength, bool deleteAllLevels, ulong expectedTotalDifficulty)
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength);
        BlockTree blockTree = blockTreeBuilder.TestObject;
        ulong chainLeft = deleteAllLevels ? 0UL : 1UL;
        for (ulong i = chainLength; i > chainLeft;)
        {
            i--;
            ChainLevelInfo? level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);
            if (level is not null)
            {
                for (int j = 0; j < level.BlockInfos.Length; j++)
                {
                    Hash256 blockHash = level.BlockInfos[j].BlockHash;
                    BlockHeader? header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
                    if (header is not null)
                    {
                        header.TotalDifficulty = null;
                    }
                }

                blockTreeBuilder.ChainLevelInfoRepository.Delete(i);
            }
        }

        Assert.That(blockTree.FindBlock(blockTree.Head!.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty, Is.EqualTo(new UInt256(expectedTotalDifficulty)));

        for (ulong i = chainLength; i > 0;)
        {
            i--;
            ChainLevelInfo? level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);

            Assert.That(level, Is.Not.Null);
            Assert.That(level!.BlockInfos.Length, Is.EqualTo(1));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Visitor_can_block_adding_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ManualResetEvent manualResetEvent = new(false);
        Task acceptTask = blockTree.Accept(new TestBlockTreeVisitor(manualResetEvent), CancellationToken.None);
        Assert.That(blockTree.CanAcceptNewBlocks, Is.False);
        manualResetEvent.Set();
        await acceptTask;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task SuggestBlockAsync_should_wait_for_blockTree_unlock()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.BlockAcceptingNewBlocks();
        ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.ReleaseAcceptingNewBlocks();
        await suggest;
        Assert.That(suggest.IsCompleted, Is.EqualTo(true));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task SuggestBlockAsync_works_well_with_multiple_locks_and_unlocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.BlockAcceptingNewBlocks();        // 1st blockade
        blockTree.ReleaseAcceptingNewBlocks();      // release - access unlocked
        blockTree.BlockAcceptingNewBlocks();        // 1st blockade
        blockTree.BlockAcceptingNewBlocks();        // 2nd blockade
        blockTree.BlockAcceptingNewBlocks();        // 3rd blockade
        ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.ReleaseAcceptingNewBlocks();      // 1st release - 2 blockades left
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.ReleaseAcceptingNewBlocks();      // 2nd release - 1 blockade left
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.BlockAcceptingNewBlocks();        // 1 more blockade - 2 blockades left
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.ReleaseAcceptingNewBlocks();      // release - 1 blockade left
        Assert.That(suggest.IsCompleted, Is.EqualTo(false));
        blockTree.ReleaseAcceptingNewBlocks();      // 3rd release - access unlocked
        await suggest;
        Assert.That(suggest.IsCompleted, Is.EqualTo(true));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task SuggestBlockAsync_works_well_when_there_are_no_blockades()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
        await suggest;
        Assert.That(suggest.IsCompleted, Is.EqualTo(true));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void SuggestBlock_should_work_with_zero_difficulty()
    {
        Block genesisWithZeroDifficulty = Build.A.Block.WithDifficulty(0).WithNumber(0).TestObject;
        CustomSpecProvider specProvider = new(((ForkActivation)0, GrayGlacier.Instance));
        specProvider.UpdateMergeTransitionInfo(null, 0);
        BlockTree blockTree = Build.A.BlockTree(genesisWithZeroDifficulty, specProvider).OfChainLength(1).TestObject;

        Block block = Build.A.Block.WithDifficulty(0).WithParent(genesisWithZeroDifficulty).TestObject;
        Assert.That(blockTree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
        Assert.That(blockTree.SuggestBlock(Build.A.Block.WithParent(block).WithDifficulty(0).TestObject), Is.EqualTo(AddBlockResult.Added));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockAddedToMain_should_have_updated_Head()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);

        ulong blockAddedToMainHeadNumber = 0ul;
        blockTree.BlockAddedToMain += (_, _) => { blockAddedToMainHeadNumber = blockTree.Head!.Header.Number; };

        AddToMain(blockTree, block1);

        Assert.That(blockAddedToMainHeadNumber, Is.EqualTo(blockTree.Head!.Number));
    }

    public static IEnumerable<TestCaseData> InvalidBlockTestCases
    {
        get
        {
            BlockHeader? FindHeader(BlockTree b, Hash256? h, BlockTreeLookupOptions o) => b.FindHeader(h, o);
            BlockHeader? FindBlock(BlockTree b, Hash256? h, BlockTreeLookupOptions o) => b.FindBlock(h, o)?.Header;

            IReadOnlyList<BlockTreeLookupOptions> valueCombinations = EnumExtensions.AllValuesCombinations<BlockTreeLookupOptions>();
            foreach (BlockTreeLookupOptions blockTreeLookupOptions in valueCombinations)
            {
                bool allowInvalid = (blockTreeLookupOptions & BlockTreeLookupOptions.AllowInvalid) == BlockTreeLookupOptions.AllowInvalid;
                yield return new TestCaseData((Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?>)FindHeader, blockTreeLookupOptions, allowInvalid)
                {
                    TestName = $"InvalidBlock_{nameof(FindHeader)}_({blockTreeLookupOptions})_{(allowInvalid ? "found" : "not_found")}"
                };
                yield return new TestCaseData((Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?>)FindBlock, blockTreeLookupOptions, allowInvalid)
                {
                    TestName = $"InvalidBlock_{nameof(FindBlock)}_({blockTreeLookupOptions})_{(allowInvalid ? "found" : "not_found")}"
                };
            }
        }
    }

    [TestCaseSource(nameof(InvalidBlockTestCases))]
    public void Find_handles_invalid_blocks(Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?> findFunction, BlockTreeLookupOptions lookupOptions, bool foundInvalid)
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Block invalidBlock = Build.A.Block.WithNumber(4).WithParent(blockTree.Head!).TestObject;
        blockTree.SuggestBlock(invalidBlock);
        blockTree.DeleteInvalidBlock(invalidBlock);
        Assert.That(findFunction(blockTree, invalidBlock.Hash, lookupOptions), Is.EqualTo(foundInvalid ? invalidBlock.Header : null));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void On_restart_loads_already_processed_genesis_block(bool wereProcessed)
    {
        TestMemDb blocksDb = new();
        TestMemDb headersDb = new();
        TestMemDb blockNumberDb = new();
        TestMemDb blocksInfosDb = new();
        ChainLevelInfoRepository chainLevelInfoRepository = new(blocksInfosDb);

        // First run
        {
            Hash256 uncleHash = new("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347");
            BlockTree tree = Build.A.BlockTree(HoodiSpecProvider.Instance)
                .WithBlockStore(new BlockStore(blocksDb))
                .WithBlocksNumberDb(blockNumberDb)
                .WithHeadersDb(headersDb)
                .WithChainLevelInfoRepository(chainLevelInfoRepository)
                .WithoutSettingHead
                .TestObject;

            // Holesky genesis
            Block genesis = new(new(
                parentHash: Keccak.Zero,
                unclesHash: uncleHash,
                beneficiary: new Address(Keccak.Zero),
                difficulty: 1,
                number: 0,
                gasLimit: 25000000,
                timestamp: 1695902100,
                extraData: [])
            {
                Hash = new Hash256("0xb5f7f912443c940f21fd611f12828d75b534364ed9e95ca4e307729a4661bde4"),
                Bloom = Core.Bloom.Empty,
                StateRoot = Keccak.EmptyTreeHash,
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                MixHash = Keccak.Zero
            });

            // Second block
            Block second = new(new(
                parentHash: genesis.Header.Hash!,
                unclesHash: uncleHash,
                beneficiary: new Address(Keccak.Zero),
                difficulty: 0,
                number: genesis.Header.Number + 1,
                gasLimit: 25000000,
                timestamp: genesis.Header.Timestamp + 100,
                extraData: [])
            {
                Hash = new Hash256("0x1111111111111111111111111111111111111111111111111111111111111111"),
                Bloom = Core.Bloom.Empty,
                StateRoot = genesis.Header.Hash,
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                MixHash = Keccak.Zero
            });

            // Third block
            Block third = new(new(
                parentHash: second.Header.Hash!,
                unclesHash: uncleHash,
                beneficiary: new Address(Keccak.Zero),
                difficulty: 0,
                number: second.Header.Number + 1,
                gasLimit: 25000000,
                timestamp: second.Header.Timestamp + 100,
                extraData: [])
            {
                Hash = new Hash256("0x2222222222222222222222222222222222222222222222222222222222222222"),
                Bloom = Core.Bloom.Empty,
                StateRoot = genesis.Header.Hash,
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                MixHash = Keccak.Zero
            });

            tree.SuggestBlock(genesis);
            Assert.That(tree.Genesis, Is.Not.Null);

            tree.TryUpdateMainChain(genesis.Header, wereProcessed, preloadedBlocks: [genesis]);

            tree.SuggestBlock(second);
            tree.SuggestBlock(third);
        }

        // Assume Nethermind got restarted
        {
            BlockTree tree = Build.A.BlockTree(HoodiSpecProvider.Instance)
                .WithBlockStore(new BlockStore(blocksDb))
                .WithBlocksNumberDb(blockNumberDb)
                .WithHeadersDb(headersDb)
                .WithChainLevelInfoRepository(chainLevelInfoRepository)
                .WithoutSettingHead
                .TestObject;

            Assert.That(tree.Genesis, Is.Not.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_insert_headers_in_batch()
    {
        BlockTree blockTree = BuildBlockTree();

        BlockHeader currentHeader = Build.A.BlockHeader.WithTotalDifficulty(1).WithDifficulty(1).WithNumber(1).TestObject;
        using ArrayPoolList<BlockHeader> batch = new(1);
        batch.Add(currentHeader);

        for (int i = 0; i < 100; i++)
        {
            currentHeader = Build.A.BlockHeader
                .WithDifficulty(1)
                .WithTotalDifficulty((ulong)(currentHeader.TotalDifficulty + 1)!)
                .WithParent(currentHeader)
                .TestObject;
            batch.Add(currentHeader);
        }

        blockTree.BulkInsertHeader(batch);

        for (ulong i = 1ul; i < 101ul; i++)
        {
            Assert.That(blockTree.FindHeader(i, BlockTreeLookupOptions.None), Is.Not.Null);
        }
    }

    private class TestBlockTreeVisitor(ManualResetEvent manualResetEvent) : IBlockTreeVisitor
    {
        private readonly ManualResetEvent _manualResetEvent = manualResetEvent;
        private bool _wait = true;

        public bool PreventsAcceptingNewBlocks => true;
        public ulong StartLevelInclusive => 0;
        public ulong EndLevelExclusive => 3;
        public async Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo? chainLevelInfo, ulong levelNumber, CancellationToken cancellationToken)
        {
            if (_wait)
            {
                await _manualResetEvent.WaitOneAsync(cancellationToken);
                _wait = false;
            }

            return LevelVisitOutcome.None;
        }

        public Task<bool> VisitMissing(Hash256 hash, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken) =>
            Task.FromResult(HeaderVisitOutcome.None);

        public Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken) =>
            Task.FromResult(BlockVisitOutcome.None);

        public Task<LevelVisitOutcome> VisitLevelEnd(
            ChainLevelInfo? chainLevelInfo, ulong levelNumber, CancellationToken cancellationToken) =>
            Task.FromResult(LevelVisitOutcome.None);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Load_SyncPivot_FromConfig()
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = 999,
            PivotHash = Hash256.Zero.ToString(),
        };
        BlockTree blockTree = Build.A.BlockTree().WithSyncConfig(syncConfig).TestObject;
        Assert.That(blockTree.SyncPivot, Is.EqualTo((999, Hash256.Zero)));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Load_SyncPivot_FromDb()
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = 999,
            PivotHash = Hash256.Zero.ToString(),
        };
        IDb metadataDb = new MemDb();
        BlockTree blockTree = Build.A.BlockTree().WithMetadataDb(metadataDb).WithSyncConfig(syncConfig).TestObject;
        blockTree.SyncPivot = (1000, TestItem.KeccakA);

        blockTree = Build.A.BlockTree().WithMetadataDb(metadataDb).WithSyncConfig(syncConfig).TestObject;
        Assert.That(blockTree.SyncPivot, Is.EqualTo((1000, TestItem.KeccakA)));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_UpdateMainBranch_UpdateSyncPivot_ToLowestPersistedHeader()
    {
        ulong pivotNumber = 3ul;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));

        Block block = Build.A.Block.Genesis.TestObject;
        Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));

        for (ulong i = 1ul; i <= 5ul; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1ul).WithParent(block).TestObject;
            Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
            tree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            tree.ForkChoiceUpdated(block.Hash, block.Hash);
            Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));
        }

        tree.BestPersistedState = 5ul;
        BlockHeader persistedStateHeader = tree.FindHeader(tree.BestPersistedState.Value, BlockTreeLookupOptions.RequireCanonical)!;

        for (ulong i = 6ul; i < 10ul; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1ul).WithParent(block).TestObject;
            tree.SuggestBlock(block);
            tree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            tree.ForkChoiceUpdated(block.Hash, block.Hash);
            Assert.That(tree.SyncPivot, Is.EqualTo((persistedStateHeader.Number, persistedStateHeader.Hash!)));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_ForkChoiceUpdated_UpdateSyncPivot_ToFinalizedHeader_BeforePersistedState()
    {
        ulong pivotNumber = 3ul;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));

        Block block = Build.A.Block.Genesis.TestObject;
        Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));

        for (ulong i = 1ul; i <= 10ul; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1ul).WithParent(block).TestObject;
            Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
            tree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));
        }

        tree.BestPersistedState = 7ul;
        BlockHeader persistedStateHeader = tree.FindHeader(tree.BestPersistedState.Value, BlockTreeLookupOptions.RequireCanonical)!;

        for (ulong i = 4ul; i < 10ul; i++)
        {
            BlockHeader header = tree.FindHeader(i, BlockTreeLookupOptions.RequireCanonical)!;
            tree.ForkChoiceUpdated(header.Hash, header.Hash);
            if (header.Number < persistedStateHeader.Number)
            {
                Assert.That(tree.SyncPivot, Is.EqualTo((header.Number, header.Hash!)));
            }
            else
            {
                Assert.That(tree.SyncPivot, Is.EqualTo((persistedStateHeader.Number, persistedStateHeader.Hash!)));
            }
        }
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_UpdateMainBranch_UpdateSyncPivot_ToHeaderUnderReorgDepth()
    {
        ulong pivotNumber = 3ul;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));

        Block block = Build.A.Block.Genesis.TestObject;
        Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));

        for (ulong i = 1ul; i <= 5ul; i++)
        {
            block = Build.A.Block
                .WithParent(block)
                .WithDifficulty(1ul)
                .WithTotalDifficulty(block.TotalDifficulty + 1ul)
                .TestObject;
            Assert.That(tree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
            tree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            Assert.That(tree.SyncPivot, Is.EqualTo((pivotNumber, TestItem.KeccakA)));
        }

        for (ulong i = 6ul; i < 100ul; i++)
        {
            block = Build.A.Block
                .WithParent(block)
                .WithDifficulty(1ul)
                .WithTotalDifficulty(block.TotalDifficulty + 1ul)
                .TestObject;
            tree.SuggestBlock(block);
            tree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            tree.BestPersistedState = block.Number;

            if (block.Number > pivotNumber + Reorganization.MaxDepth)
            {
                BlockHeader reorgDepthHeader = tree.FindHeader(block.Number - Reorganization.MaxDepth, BlockTreeLookupOptions.RequireCanonical)!;
                Assert.That(tree.SyncPivot, Is.EqualTo((reorgDepthHeader.Number, reorgDepthHeader.Hash!)));
            }
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_WhenThirdOfThreeSiblingsIsCanonical_ReturnsThatSibling()
    {
        // Exercises SwapToMain with index > 1 (three blocks at the same height,
        // the third one — index=2 — is made canonical last).
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.TryUpdateMainChain(blockA.Header, true, preloadedBlocks: new[] { blockA });

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.TryUpdateMainChain(blockB.Header, true, preloadedBlocks: new[] { blockB });

        Block blockC = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 3 }).TestObject;
        blockTree.SuggestBlock(blockC);
        blockTree.TryUpdateMainChain(blockC.Header, true, preloadedBlocks: new[] { blockC });

        using (Assert.EnterMultipleScope())
        {
            // C (the third sibling, originally at index 2) must now be canonical
            Block? byNumber = blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);
            Assert.That(byNumber, Is.Not.Null, "RequireCanonical lookup must find C");
            Assert.That(byNumber!.Hash, Is.EqualTo(blockC.Hash!), "C is the last canonical, SwapToMain must have moved it to index 0");

            // A and B must not be canonical
            Assert.That(blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null, "A must not be canonical after C was set");
            Assert.That(blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null, "B must not be canonical after C was set");

            // All three are still findable by hash (non-canonical lookup)
            Assert.That(blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.None), Is.Not.Null, "A findable by hash");
            Assert.That(blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.None), Is.Not.Null, "B findable by hash");
            Assert.That(blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.None), Is.Not.Null, "C findable by hash");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenCalledWithWereProcessedFalse_MarksBlockCanonical()
    {
        // wereProcessed=false is used during sync to set canonical without updating Head.
        // The canonical marker (HasBlockOnMainChain / BlockInfos[0]) must be set regardless.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.TryUpdateMainChain(blockA.Header, wereProcessed: true, preloadedBlocks: new[] { blockA });

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);

        // Reorg to B with wereProcessed=false (sync path)
        blockTree.TryUpdateMainChain(blockB.Header, wereProcessed: false, preloadedBlocks: new[] { blockB });

        using (Assert.EnterMultipleScope())
        {
            // Canonical marker must be updated even without wereProcessed
            Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blockB.Hash!), "B must be canonical at height 1 even when TryUpdateMainChain was called with wereProcessed=false");

            Assert.That(blockTree.IsMainChain(blockB.Header), Is.True, "B is canonical");
            Assert.That(blockTree.IsMainChain(blockA.Header), Is.False, "A is no longer canonical");
        }
    }

    [TestCase(1ul, false, TestName = "SingleDescendant")]
    [TestCase(3ul, false, TestName = "MultipleDescendants")]
    [TestCase(3ul, true, TestName = "MultipleDescendantsWithGap")]
    [MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenBeaconSyncMarksThenReorgsToSibling_ClearsStaleMarkers(ulong descendantCount, bool simulateGap)
    {
        // Beacon sync marks N descendants canonical (wereProcessed=false, Head stays stale at H=1).
        // FCU reorgs to sibling at the same height. All stale markers must be cleared.
        // When simulateGap=true, a concurrent MoveToMain clears one intermediate marker,
        // creating a gap that the bounded scan must handle.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block headBlock = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xAA]).TestObject;
        blockTree.SuggestBlock(headBlock);

        Block[] descendants = BuildAndSuggestChain(blockTree, headBlock, (int)descendantCount);

        // FCU sets Head to headBlock at H=1
        blockTree.TryUpdateMainChain(headBlock.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { headBlock });
        Assert.That(blockTree.Head!.Hash, Is.EqualTo(headBlock.Hash!));

        // Beacon sync: mark descendants canonical without advancing Head
        foreach (Block d in descendants)
        {
            blockTree.TryUpdateMainChain(d.Header, wereProcessed: false, preloadedBlocks: new[] { d });
        }

        Assert.That(blockTree.Head!.Number, Is.EqualTo(1), "Head must stay at H=1 — wereProcessed=false");
        foreach (Block d in descendants)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.True, $"precondition: block at H={d.Number} canonical via beacon sync");
        }

        if (simulateGap && descendantCount >= 3)
        {
            // Simulate race: concurrent MoveToMain clears middle marker, creating a gap
            ChainLevelInfo? gapLevel = blockTree.FindLevel(descendants[1].Number);
            gapLevel!.HasBlockOnMainChain = false;
            Assert.That(blockTree.IsMainChain(descendants[1].Header), Is.False, "precondition: gap exists");
        }

        // FCU reorg to sibling at H=1
        Block sibling = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xBB }).TestObject;
        blockTree.SuggestBlock(sibling);
        blockTree.TryUpdateMainChain(sibling.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { sibling });

        Assert.That(blockTree.Head!.Hash, Is.EqualTo(sibling.Hash!));
        Assert.That(blockTree.IsMainChain(sibling.Header), Is.True, "sibling must be canonical");
        foreach (Block d in descendants)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.False, $"block at H={d.Number} must be de-canonicalized after reorg");
        }

        // FindCanonicalBlockInfo must return null for all orphaned heights
        for (ulong h = 2; h <= descendantCount + 1; h++)
        {
            Assert.That(blockTree.FindCanonicalBlockInfo(h), Is.Null, $"H={h} must return null — orphaned after reorg");
        }

        // Canonical lookup at H=1 must return sibling
        BlockInfo? infoAt1 = blockTree.FindCanonicalBlockInfo(1ul);
        Assert.That(infoAt1, Is.Not.Null);
        Assert.That(infoAt1!.BlockHash, Is.EqualTo(sibling.Hash!), "H=1 must return sibling's hash");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenFcuToAncestorWithStaleBeaconSyncedDescendants_ClearsAll()
    {
        // ePBS scenario: FCU can reorg to an ancestor (not just a sibling at the same height).
        // If head is stale because beacon sync marked descendants canonical without updating Head,
        // a subsequent FCU to an ancestor must clear ALL canonical markers above the ancestor —
        // including beacon-synced blocks above the stale head that the IF branch cannot reach.
        //
        // Scenario:
        //   genesis → b1(H=1) → b2(H=2) → b3(H=3) → b4(H=4)
        //
        //   TryUpdateMainChain([b1], wereProcessed: true)   — FCU(b1): head = b1 at H=1.
        //   TryUpdateMainChain([b2], wereProcessed: false)  — beacon sync: b2 canonical, head stays at b1.
        //   TryUpdateMainChain([b3], wereProcessed: false)  — beacon sync: b3 canonical, head stays at b1.
        //   TryUpdateMainChain([b4], wereProcessed: false)  — beacon sync: b4 canonical, head stays at b1.
        //   TryUpdateMainChain([genesis], wereProcessed: true) — ePBS FCU to ancestor at H=0:
        //     previousHeadNumber(1) > lastNumber(0) → IF branch clears H=1 only.
        //     b2, b3, b4 are NOT cleared — they are above the stale head and invisible to the IF branch.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 4);

        // FCU(b1): head = b1 at H=1.
        blockTree.TryUpdateMainChain(chain[0].Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { chain[0] });

        // Beacon sync: b2, b3, b4 marked canonical without updating Head.
        for (int i = 1; i < chain.Length; i++)
        {
            blockTree.TryUpdateMainChain(chain[i].Header, wereProcessed: false, preloadedBlocks: new[] { chain[i] });
        }

        // Preconditions: head stale at b1, b2-b4 canonical via beacon sync.
        Assert.That(blockTree.Head!.Hash, Is.EqualTo(chain[0].Hash!), "precondition: head stale at b1");
        Assert.That(blockTree.FindBlock(chain[1].Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "precondition: b2 beacon-synced canonical");
        Assert.That(blockTree.FindBlock(chain[3].Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "precondition: b4 beacon-synced canonical");

        // ePBS FCU to ancestor: reorg back to genesis at H=0.
        blockTree.TryUpdateMainChain(genesis.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { genesis });

        Assert.That(blockTree.FindBlock(genesis.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "genesis must be canonical");
        foreach (Block b in chain)
        {
            Assert.That(blockTree.FindBlock(b.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null, $"b{b.Number} must be de-canonicalized");
        }
    }

    [TestCase(1, TestName = "SingleStaleLevel")]
    [TestCase(3, TestName = "MultipleStaleLevel")]
    [MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenStaleLevelsAboveHead_ClearsAll(int staleLevelCount)
    {
        // Sync (wereProcessed=false) marks N levels above head canonical without updating Head.
        // HealCanonicalChain must scan upward and clear all stale markers.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block head = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        blockTree.SuggestBlock(head);

        Block[] descendants = BuildAndSuggestChain(blockTree, head, staleLevelCount);

        // FCU: head at H=1
        blockTree.TryUpdateMainChain(head.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { head });

        // Sync marks descendants canonical without updating Head
        foreach (Block d in descendants)
        {
            blockTree.TryUpdateMainChain(d.Header, wereProcessed: false, preloadedBlocks: new[] { d });
        }

        blockTree.HealCanonicalChain(head.Hash!, maxBlockDepth: 10);

        foreach (Block d in descendants)
        {
            Assert.That(blockTree.FindBlock(d.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null, $"H={d.Number} stale marker must be cleared");
        }

        Assert.That(blockTree.FindBlock(head.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "head must remain canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ClearStaleMarkersAbove_DoesNotScanPastBestKnownNumber()
    {
        // Regression: scan must cap at Max(BestKnownNumber, BestKnownBeaconNumber) so a corrupted
        // DB cannot drive an unbounded loop. A stray marker far above must survive the heal.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block head = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        blockTree.SuggestBlock(head);
        blockTree.TryUpdateMainChain(head.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { head });

        const long strayHeight = 1_000_000L;
        ChainLevelInfoRepository repo = new(_blocksInfosDb);
        ChainLevelInfo strayLevel = new(true, [new BlockInfo(TestItem.KeccakA, UInt256.One)]);
        repo.PersistLevel(strayHeight, strayLevel);

        blockTree.HealCanonicalChain(head.Hash!, maxBlockDepth: 10);

        ChainLevelInfo? afterHeal = new ChainLevelInfoRepository(_blocksInfosDb).LoadLevel(strayHeight);
        Assert.That(afterHeal, Is.Not.Null, "stray level must remain in DB — bounded scan must not reach it");
        Assert.That(afterHeal!.HasBlockOnMainChain, Is.True, "scan must not have cleared markers beyond BestKnownNumber");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenWrongBlockIsMarkedCanonical_FixesMarker()
    {
        // Scenario: A and B are siblings at H=1. B was swapped to index 0 by accident
        // (e.g. a stale write), but the real canonical chain goes through A.
        // HealCanonicalChain walking from A must swap A back to index 0.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;

        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockB);

        // Make A canonical first, then B (leaving B at index 0, A at index 1)
        blockTree.TryUpdateMainChain(blockA.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { blockA });
        blockTree.TryUpdateMainChain(blockB.Header, wereProcessed: false, preloadedBlocks: new[] { blockB }); // B wrongly becomes canonical

        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blockB.Hash!), "precondition: B is wrongly canonical");

        blockTree.HealCanonicalChain(blockA.Hash!, maxBlockDepth: 10);

        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blockA.Hash!), "A must be canonical after heal");
        Assert.That(blockTree.IsMainChain(blockB.Header), Is.False, "B must not be canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenChainIsAlreadyConsistent_MakesNoChanges()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 2);
        blockTree.TryUpdateMainChain(chain[^1].Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: chain);

        blockTree.HealCanonicalChain(chain[1].Hash!, maxBlockDepth: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FindBlock(chain[1].Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "b2 must remain canonical");
            Assert.That(blockTree.FindBlock(chain[0].Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "b1 must remain canonical");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenStartHashIsUnknown_DoesNothing()
    {
        (BlockTree blockTree, _) = BuildBlockTreeWithGenesis();

        // Should not throw — unknown hash is treated as a no-op.
        Assert.That(() => blockTree.HealCanonicalChain(TestItem.KeccakA, maxBlockDepth: 10), Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenStaleMarkersAboveAndIncorrectMarkersBelow_FixesBothDirections()
    {
        // Combined scenario: wrong canonical at H=1 AND stale markers at H=2,3 above head.
        // This is the realistic production scenario: FCU moved head to B at H=1,
        // but C (child of A) was left canonical at H=2 by sync, and A is still canonical at H=1
        // when it shouldn't be.
        //
        //   genesis → A(H=1) → C(H=2)   ← sync left C canonical, A at H=1
        //           → B(H=1)             ← heal starts from B, the correct head
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        Block blockC = Build.A.Block.WithNumber(2).WithParent(blockA).TestObject;

        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockB);
        blockTree.SuggestBlock(blockC);

        // FCU(A): A is canonical at H=1, B is known but not canonical.
        // Sync marks C canonical at H=2 without updating Head.
        // No FCU for B — the heal is told B is the correct head (e.g. via the CL reorg).
        blockTree.TryUpdateMainChain(blockA.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { blockA });
        blockTree.TryUpdateMainChain(blockC.Header, wereProcessed: false, preloadedBlocks: new[] { blockC }); // sync: C canonical at H=2, head stays A

        // Preconditions: A canonical at H=1, C stale-canonical at H=2, B suggested but not canonical
        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blockA.Hash!), "precondition: A is canonical at H=1");
        Assert.That(blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Not.Null, "precondition: C is stale-canonical at H=2");

        // Heal from B — the CL says B is the correct head.
        // Must both: clear C at H=2 (upward scan) and swap B to index 0 at H=1 (downward walk).
        blockTree.HealCanonicalChain(blockB.Hash!, maxBlockDepth: 10);

        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(blockB.Hash!), "B must be canonical at H=1 after heal");
        Assert.That(blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical), Is.Null, "C must not be canonical — it was orphaned when B replaced A");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenDepthExceedsMaxBlockDepth_StopsAtLimit()
    {
        // maxBlockDepth=0 repairs only the start block; maxBlockDepth=N repairs start + N parents.
        // A broken marker one level below start must NOT be repaired when maxBlockDepth=0.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block b1Alt = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;

        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b1Alt);
        blockTree.SuggestBlock(b2);

        // b1Alt wrongly canonical at H=1, b2 canonical at H=2 (head)
        blockTree.TryUpdateMainChain(b1.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { b1 });
        blockTree.TryUpdateMainChain(b2.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { b2 });
        blockTree.TryUpdateMainChain(b1Alt.Header, wereProcessed: false, preloadedBlocks: new[] { b1Alt }); // breaks H=1

        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(b1Alt.Hash!), "precondition: H=1 is broken");

        // Heal from b2 with depth=0: only checks b2, does not reach H=1
        blockTree.HealCanonicalChain(b2.Hash!, maxBlockDepth: 0);

        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(b1Alt.Hash!), "H=1 must remain broken — it is beyond maxBlockDepth");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenBeaconSyncAndFcuCycleRepeatedTwice_ClearsStaleMarkersEachRound()
    {
        // Two full beacon-sync + FCU cycles at the same head height (H=1).
        // Each round: beacon sync marks descendants canonical, then FCU reorgs to a new sibling.
        // After each FCU, all stale markers from that round must be cleared before the next round.
        //
        // Round 1:
        //   FCU(head): head=H=1
        //   Beacon sync: desc1[0] (H=2), desc1[1] (H=3) marked canonical without updating Head.
        //   FCU(sibling1): reorg to sibling1 at H=1 — stale desc1 markers must be cleared.
        //
        // Round 2 (starting from sibling1 as new head):
        //   Beacon sync: desc2[0] (H=2), desc2[1] (H=3) from sibling1's chain, marked canonical.
        //   FCU(sibling2): reorg to sibling2 at H=1 — stale desc2 markers must be cleared.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block head = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xAA]).TestObject;
        blockTree.SuggestBlock(head);
        blockTree.TryUpdateMainChain(head.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { head });

        // Round 1 — beacon sync marks two descendants of head canonical
        Block[] desc1 = BuildAndSuggestChain(blockTree, head, 2);
        foreach (Block d in desc1)
        {
            blockTree.TryUpdateMainChain(d.Header, wereProcessed: false, preloadedBlocks: new[] { d });
        }

        Assert.That(blockTree.Head!.Hash, Is.EqualTo(head.Hash!), "precondition: head stale at H=1 after round-1 beacon sync");
        foreach (Block d in desc1)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.True, $"precondition: round-1 desc at H={d.Number} canonical via beacon sync");
        }

        // Round 1 FCU — reorg to sibling1 at H=1
        Block sibling1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xBB]).TestObject;
        blockTree.SuggestBlock(sibling1);
        blockTree.TryUpdateMainChain(sibling1.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { sibling1 });

        Assert.That(blockTree.Head!.Hash, Is.EqualTo(sibling1.Hash!), "after round-1 FCU head must be sibling1");
        foreach (Block d in desc1)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.False, $"round-1 stale marker at H={d.Number} must be cleared after FCU to sibling1");
        }

        // Round 2 — beacon sync marks two descendants of sibling1 canonical
        Block[] desc2 = BuildAndSuggestChain(blockTree, sibling1, 2);
        foreach (Block d in desc2)
        {
            blockTree.TryUpdateMainChain(d.Header, wereProcessed: false, preloadedBlocks: new[] { d });
        }

        Assert.That(blockTree.Head!.Hash, Is.EqualTo(sibling1.Hash!), "precondition: head stale at sibling1 after round-2 beacon sync");
        foreach (Block d in desc2)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.True, $"precondition: round-2 desc at H={d.Number} canonical via beacon sync");
        }

        // Round 2 FCU — reorg to sibling2 at H=1
        Block sibling2 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xCC]).TestObject;
        blockTree.SuggestBlock(sibling2);
        blockTree.TryUpdateMainChain(sibling2.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { sibling2 });

        Assert.That(blockTree.Head!.Hash, Is.EqualTo(sibling2.Hash!), "after round-2 FCU head must be sibling2");
        foreach (Block d in desc2)
        {
            Assert.That(blockTree.IsMainChain(d.Header), Is.False, $"round-2 stale marker at H={d.Number} must be cleared after FCU to sibling2");
        }

        // Sibling2 is canonical at H=1; head, sibling1 are orphaned
        Assert.That(blockTree.IsMainChain(sibling2.Header), Is.True, "sibling2 must be canonical at H=1");
        Assert.That(blockTree.IsMainChain(head.Header), Is.False, "original head must be orphaned");
        Assert.That(blockTree.IsMainChain(sibling1.Header), Is.False, "sibling1 must be orphaned");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenForwardProcessingWithBeaconSyncedDescendants_DoesNotClearMarkers()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 4);
        blockTree.TryUpdateMainChain(chain[0].Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { chain[0] });
        for (int i = 1; i < chain.Length; i++)
            blockTree.TryUpdateMainChain(chain[i].Header, wereProcessed: false, preloadedBlocks: new[] { chain[i] });

        // Forward processing H=2 (forceUpdateHeadBlock: false) must not clear H=3, H=4
        blockTree.TryUpdateMainChain(chain[1].Header, wereProcessed: true, forceUpdateHeadBlock: false, preloadedBlocks: new[] { chain[1] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.IsMainChain(chain[2].Header), Is.True, "H=3 marker must survive");
            Assert.That(blockTree.IsMainChain(chain[3].Header), Is.True, "H=4 marker must survive");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TryUpdateMainChain_WhenFcuForwardReorgToLongerChain_ClearsStaleMarkersAboveNewHead()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block[] chainA = BuildAndSuggestChain(blockTree, genesis, 4);
        blockTree.TryUpdateMainChain(chainA[0].Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { chainA[0] });
        for (int i = 1; i < chainA.Length; i++)
            blockTree.TryUpdateMainChain(chainA[i].Header, wereProcessed: false, preloadedBlocks: new[] { chainA[i] });

        // FCU to chain B at H=3 (forceUpdateHeadBlock: true) must clear A4
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xBB]).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).WithExtraData([0xBB]).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).WithExtraData([0xBB]).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.SuggestBlock(b3);
        blockTree.TryUpdateMainChain(b3.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: new[] { b1, b2, b3 });

        Assert.That(blockTree.IsMainChain(chainA[3].Header), Is.False, "A4 stale marker must be cleared");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_WhenBlockOrphanedAfterReorgInPoS_ReturnsNull()
    {
        // In PoS all blocks share the same cumulative TotalDifficulty (difficulty=0 per block).
        // The PoW-era "best difficulty" fallback in GetBlockHashOnMainOrBestDifficultyHash
        // fires when HasBlockOnMainChain=false and returns whichever block it finds first —
        // which after a reorg is the now-orphaned block. FindBlock(height, None) must return
        // null for a decanonized height in PoS, not the orphaned block.
        CustomSpecProvider specProvider = new(((ForkActivation)0, London.Instance))
        {
            TerminalTotalDifficulty = UInt256.Zero  // pure PoS from genesis (e.g. Gnosis)
        };

        _blocksDb = new TestMemDb();
        _headersDb = new TestMemDb();
        _blocksInfosDb = new TestMemDb();
        BlockTree blockTree = Build.A.BlockTree(specProvider)
            .WithBlocksDb(_blocksDb)
            .WithHeadersDb(_headersDb)
            .WithBlockInfoDb(_blocksInfosDb)
            .WithoutSettingHead
            .TestObject;

        Block genesis = Build.A.Block.WithNumber(0).WithDifficulty(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.TryUpdateMainChain(genesis.Header, true, preloadedBlocks: new[] { genesis });

        // Old chain: genesis → A1 → A2 (head at height 2)
        Block a1 = Build.A.Block.WithNumber(1).WithDifficulty(0).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a1);
        Block a2 = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(a1).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a2);
        blockTree.TryUpdateMainChain(a2.Header, true, preloadedBlocks: new[] { a1, a2 });

        // Reorg: genesis → B1 (head drops from height 2 to height 1, different block)
        Block b1 = Build.A.Block.WithNumber(1).WithDifficulty(0).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.TryUpdateMainChain(b1.Header, true, preloadedBlocks: new[] { b1 });

        using (Assert.EnterMultipleScope())
        {
            // Height 2 was orphaned: must return null, not the stale A2
            Assert.That(blockTree.FindBlock(2, BlockTreeLookupOptions.None), Is.Null, "orphaned height 2 must return null after reorg in PoS — not the stale A2 block");

            // Height 1 must return the new canonical B1
            Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(b1.Hash!), "height 1 must return B1 after reorg");
        }
    }

    private static void AssertSuggestNotifications(AddBlockResult result, bool hasNotified, bool hasNotifiedNewSuggested)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hasNotified, Is.True, "notification");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
        }
    }
}
