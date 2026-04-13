// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
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
using Nethermind.Db.Blooms;
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
        blockTree.UpdateMainChain(new[] { block0 }, true);
    }

    private (BlockTree blockTree, Block genesis) BuildBlockTreeWithGenesis(bool forceUpdateHead = false)
    {
        BlockTree blockTree = BuildBlockTree();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: forceUpdateHead);
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
    public void Add_genesis_shall_notify()
    {
        bool hasNotified = false;
        BlockTree blockTree = BuildBlockTree();
        blockTree.NewHeadBlock += (_, _) => { hasNotified = true; };

        bool hasNotifiedNewSuggested = false;
        blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

        Block block = Build.A.Block.WithNumber(0).TestObject;
        AddBlockResult result = blockTree.SuggestBlock(block);
        blockTree.UpdateMainChain(block);

        Assert.That(hasNotified, Is.True, "notification");
        Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
        Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
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

        Assert.That(hasNotified, Is.True, "notification");
        Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
        Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Suggesting_genesis_many_times_does_not_cause_any_trouble()
    {
        BlockTree blockTree = BuildBlockTree();
        Block blockA = Build.A.Block.WithNumber(0).TestObject;
        Block blockB = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(blockA).Should().Be(AddBlockResult.Added);
        blockTree.SuggestBlock(blockB).Should().Be(AddBlockResult.AlreadyKnown);
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
        blockTree.UpdateMainChain(block1);

        Assert.That(hasNotified, Is.True, "notification");
        Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
        Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
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

        blockTree.SuggestBlock(block0);
        blockTree.NewHeadBlock += (_, _) => { newHeadBlockNotifications++; };
        blockTree.BlockAddedToMain += (_, _) => { blockAddedToMainNotifications++; };

        blockTree.SuggestBlock(block1);
        blockTree.SuggestBlock(block2);
        blockTree.SuggestBlock(block3);
        blockTree.UpdateMainChain(new[] { block1, block2, block3 }, true);

        newHeadBlockNotifications.Should().Be(1, "new head block");
        blockAddedToMainNotifications.Should().Be(3, "block added to main");
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

        Assert.That(hasNotified, Is.True, "notification");
        Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
        Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
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

        Assert.That(hasNotifiedBest, Is.False, "notification best");
        Assert.That(hasNotifiedHead, Is.False, "notification head");
        Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
        Assert.That(hasNotifiedNewSuggested, Is.True, "NewSuggestedBlock");
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

        Assert.That(tree2.BestKnownNumber, Is.EqualTo(3L), "best known");
        Assert.That(tree2.Head?.Number, Is.EqualTo(0), "head");
        Assert.That(tree2.BestSuggestedHeader!.Hash, Is.EqualTo(block3B.Hash), "suggested");

        blockStore.Get(block1.Number, block1.Hash!).Should().BeNull("block 1");
        blockStore.Get(block2.Number, block2.Hash!).Should().BeNull("block 2");
        blockStore.Get(block3.Number, block3.Hash!).Should().BeNull("block 3");

        Assert.That(blockInfosDb.Get(1), Is.Not.Null, "level 1");
        Assert.That(blockInfosDb.Get(2), Is.Not.Null, "level 2");
        Assert.That(blockInfosDb.Get(3), Is.Not.Null, "level 3");
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
        Assert.That(headers.Count, Is.EqualTo(2));
        Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
        Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
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
        Assert.That(headers.Count, Is.EqualTo(2));
        Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
        Assert.That(headers[1].Hash, Is.EqualTo(block2.Hash));
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
        Assert.That(headers.Count, Is.EqualTo(2));
        Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
        Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
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
        Assert.That(headers.Count, Is.EqualTo(2));
        Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
        Assert.That(headers[1].Hash, Is.EqualTo(block0.Hash));
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
        Assert.That(headers.Count, Is.EqualTo(2));
        Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
        Assert.That(headers[1], Is.Null);
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
        Assert.That(headers.Count, Is.EqualTo(100));
        Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
        Assert.That(headers[3], Is.Null);

        Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
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
        Assert.That(headers.Count, Is.EqualTo(100));
        Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
        Assert.That(headers[3], Is.Null);

        Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
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
        Assert.That(blocks.Count, Is.EqualTo(length));
        Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
        Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
        Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
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
        Assert.That(blocks.Count, Is.EqualTo(length));
        Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block1.Hash));
        Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
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
        Assert.That(blocks.Count, Is.EqualTo(length));
        Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
        Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
        Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
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
        Assert.That(blocks.Count, Is.EqualTo(3));

        Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block2.Hash));
        Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block0.Hash));
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
        Assert.That(blocks.Count, Is.EqualTo(2), "length");
        Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
        Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
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
        block1.TotalDifficulty.Should().NotBeNull();
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

        Assert.That(blockTree.Head!.CalculateHash(), Is.EqualTo(block0.Hash), "head block");
        Assert.That(blockTree.BestSuggestedHeader!.CalculateHash(), Is.EqualTo(block1.Hash), "best suggested");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Sets_genesis_block()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        AddToMain(blockTree, block0);

        Assert.That(blockTree.Genesis!.CalculateHash(), Is.EqualTo(block0.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ForkChoiceUpdated_update_hashes()
    {
        BlockTree blockTree = BuildBlockTree();
        Hash256 finalizedBlockHash = TestItem.KeccakB;
        Hash256 safeBlockHash = TestItem.KeccakC;
        blockTree.ForkChoiceUpdated(finalizedBlockHash, safeBlockHash);
        Assert.That(blockTree.FinalizedHash, Is.EqualTo(finalizedBlockHash));
        Assert.That(blockTree.SafeHash, Is.EqualTo(safeBlockHash));
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
        Assert.That(blockTree.Head?.Hash, Is.EqualTo(headBlock.Hash), "head");
        Assert.That(blockTree.Genesis?.Hash, Is.EqualTo(headBlock.Hash), "genesis");
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
        blockTree.UpdateMainChain(new[] { block0, block1 }, true);
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
        blockTree.UpdateMainChain(block1);
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
        blockTree.UpdateMainChain(block0);
        blockTree.BestSuggestedHeader.Should().Be(block1.Header);
        blockTree.PendingHash.Should().Be(block0.Hash!);
        Block? pending = ((IBlockFinder)blockTree).FindPendingBlock();
        pending!.Header.Should().BeSameAs(block0.Header);
        pending.Body.Should().Be(block0.Body);
        ((IBlockFinder)blockTree).FindPendingHeader().Should().BeSameAs(block0.Header);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Is_main_chain_returns_true_on_fast_sync_block()
    {
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        BlockTree blockTree = BuildBlockTree();
        blockTree.SuggestBlock(block0, BlockTreeSuggestOptions.None);
        blockTree.IsMainChain(block0.Hash!).Should().BeTrue();
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
        blockTree.UpdateMainChain(block1);

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

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.DeleteInvalidBlock(block2);

        Assert.That(tree.BestKnownNumber, Is.EqualTo(block1.Number));
        Assert.That(tree.Head?.Header, Is.EqualTo(block1.Header));
        Assert.That(tree.BestSuggestedHeader, Is.EqualTo(block1.Header));
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

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.DeleteInvalidBlock(block2);

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

        tree.UpdateMainChain(block0);
        tree.UpdateMainChain(block1);
        tree.DeleteInvalidBlock(block1b);

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

        repository.LoadLevel(1)!.BlockInfos.Length.Should().Be(1);
        repository.LoadLevel(2)!.BlockInfos.Length.Should().Be(1);
        repository.LoadLevel(3)!.BlockInfos.Length.Should().Be(1);
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

        tree.UpdateMainChain(block5);
        tree.DeleteInvalidBlock(block3bad);

        Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "best known");
        Assert.That(tree.Head?.Header, Is.EqualTo(block5.Header), "head");
        Assert.That(tree.BestSuggestedHeader!.Hash, Is.EqualTo(block5.Hash), "suggested");
    }

    [Test, MaxTime(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
    public void When_lowestInsertedHeaderWasNotPersisted_useBinarySearchToLoadLowestInsertedHeader(long beginIndex, long insertedBlocks)
    {
        long? expectedResult = insertedBlocks == 0L ? null : beginIndex - insertedBlocks + 1L;

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

        for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
        {
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

        for (int i = 1; i < 100; i++)
        {
            tree.Insert(Build.A.BlockHeader.WithNumber(i).WithParent(tree.FindHeader(i - 1, BlockTreeLookupOptions.None)!).TestObject);
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

    [TestCase(5, 10)]
    [TestCase(10, 10)]
    [TestCase(12, 0)]
    public void Does_not_load_bestKnownNumber_before_syncPivot(long syncPivot, long expectedBestKnownNumber)
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
        new object[] {1L, 0L},
        new object[] {1L, 1L},
        new object[] {2L, 0L},
        new object[] {2L, 1L},
        new object[] {2L, 2L},
        new object[] {3L, 0L},
        new object[] {3L, 1L},
        new object[] {3L, 2L},
        new object[] {3L, 3L},
        new object[] {4L, 0L},
        new object[] {4L, 1L},
        new object[] {4L, 2L},
        new object[] {4L, 3L},
        new object[] {4L, 4L},
        new object[] {5L, 0L},
        new object[] {5L, 1L},
        new object[] {5L, 2L},
        new object[] {5L, 3L},
        new object[] {5L, 4L},
        new object[] {5L, 5L},
        new object[] {728000, 0L},
        new object[] {7280000L, 1L}
    };

    [Test, MaxTime(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
    public void Loads_best_known_correctly_on_inserts(long beginIndex, long insertedBlocks)
    {
        long expectedResult = insertedBlocks == 0L ? 0L : beginIndex;

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

        for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
            tree.Insert(block.Header);
            tree.Insert(block);
        }

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.BestKnownNumber, Is.EqualTo(expectedResult), "tree");
        Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(expectedResult), "loaded tree");
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

        List<Block> blocks = new() { genesis };

        for (long i = 1; i < 100; i++)
        {
            Block block = Build.A.Block
                .WithNumber(i)
                .WithParent(parent)
                .WithTotalDifficulty(i).TestObject;
            blocks.Add(block);
            parent = block;
            if (i <= 50)
            {
                // tree.Insert(block.Header);
                tree.SuggestBlock(block);
            }
            else
            {
                tree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconBodyMetadata);
            }
        }
        tree.UpdateMainChain(blocks.ToArray(), true);
        tree.BestPersistedState = 50;

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(loadedTree.Head?.Number, Is.EqualTo(50));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(1L)]
    [TestCase(2L)]
    [TestCase(3L)]
    public void Loads_best_known_correctly_on_inserts_followed_by_suggests(long pivotNumber)
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
        for (long i = pivotNumber; i > 0; i--)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
            pivotBlock ??= block;
            tree.Insert(block.Header);
        }

        tree.SuggestHeader(Build.A.BlockHeader.WithNumber(pivotNumber + 1).WithParent(pivotBlock!.Header).TestObject);

        BlockTree loadedTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithDatabaseFrom(builder)
            .WithSyncConfig(syncConfig)
            .TestObject;

        Assert.That(tree.BestKnownNumber, Is.EqualTo(pivotNumber + 1), "tree");
        Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(pivotNumber + 1), "loaded tree");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Loads_best_known_correctly_when_head_before_pivot()
    {
        int pivotNumber = 1000;
        int head = 10;
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
        long pivotNumber = 0L;

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
        Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis));
        Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis.Header));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_set_zero_total_difficulty()
    {
        long pivotNumber = 0L;

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
        tree.SuggestBlock(genesis).Should().Be(AddBlockResult.Added);
        tree.FindBlock(genesis.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be(UInt256.Zero);

        Block A = Build.A.Block.WithParent(genesis).WithDifficulty(0).TestObject;
        tree.SuggestBlock(A).Should().Be(AddBlockResult.Added);
        tree.FindBlock(A.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be(UInt256.Zero);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Inserts_blooms()
    {
        long pivotNumber = 5L;

        SyncConfig syncConfig = new()
        {
            PivotNumber = pivotNumber,
        };

        IBloomStorage bloomStorage = Substitute.For<IBloomStorage>();
        IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();

        BlockTree tree = Build.A.BlockTree()
            .WithChainLevelInfoRepository(chainLevelInfoRepository)
            .WithBloomStorage(bloomStorage)
            .WithSyncConfig(syncConfig)
            .TestObject;

        tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        for (long i = 5; i > 0; i--)
        {
            Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(1L).TestObject;
            tree.Insert(block.Header);
            Received.InOrder(() =>
            {
                bloomStorage.Store(block.Header.Number, block.Bloom!);
                chainLevelInfoRepository.PersistLevel(block.Header.Number, Arg.Any<ChainLevelInfo>(), Arg.Any<BatchWrite>());
            });
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Block_loading_is_lazy()
    {
        SyncConfig syncConfig = new()
        {
            PivotNumber = 0L,
        };

        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithSyncConfig(syncConfig);
        BlockTree tree = builder
            .TestObject;

        Block genesis = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(genesis);

        Block previousBlock = genesis;
        for (int i = 1; i < 10; i++)
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
    public void When_block_is_moved_to_main_blooms_are_stored()
    {
        Transaction t1 = Build.A.Transaction.TestObject;
        Transaction t2 = Build.A.Transaction.TestObject;

        IBloomStorage bloomStorage = Substitute.For<IBloomStorage>();
        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithBloomStorage(bloomStorage)
            .TestObject;
        // new(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, bloomStorage, LimboLogs.Instance);
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1A = Build.A.Block.WithNumber(1).WithDifficulty(2).WithTransactions(t1).WithParent(block0).TestObject;
        Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithTransactions(t2).WithParent(block0).TestObject;

        AddToMain(blockTree, block0);

        blockTree.SuggestBlock(block1B);
        blockTree.SuggestBlock(block1A);
        blockTree.UpdateMainChain(block1A);

        bloomStorage.Received().Store(block1A.Number, block1A.Bloom!);
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_find_genesis_level()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ChainLevelInfo info = blockTree.FindLevel(0)!;
        Assert.That(info.HasBlockOnMainChain, Is.True);
        Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_find_some_level()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ChainLevelInfo info = blockTree.FindLevel(1)!;
        Assert.That(info.HasBlockOnMainChain, Is.True);
        Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
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
        Assert.That(blockTree.FindBlock(2, BlockTreeLookupOptions.None), Is.Null);
        Assert.That(blockTree.FindHeader(2, BlockTreeLookupOptions.None), Is.Null);
        Assert.That(blockTree.FindLevel(2), Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Does_not_delete_outside_of_the_slice()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.DeleteChainSlice(2, 2);
        Assert.That(blockTree.FindBlock(1, BlockTreeLookupOptions.None), Is.Not.Null);
        Assert.That(blockTree.FindHeader(1, BlockTreeLookupOptions.None), Is.Not.Null);
        Assert.That(blockTree.FindLevel(1), Is.Not.Null);
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
        Assert.That(blockTree.FindLevel(1), Is.Null);
        Assert.That(blockTree.FindLevel(2), Is.Null);
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
    public void Throws_when_start_below_zero()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(-1, 1));
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
        blockTree.SuggestBlock(Build.A.Block.WithNumber(3).TestObject).Should().Be(AddBlockResult.CannotAccept);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_block_cannot_insert_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.CanAcceptNewBlocks.Should().BeTrue();
        blockTree.BlockAcceptingNewBlocks();
        blockTree.CanAcceptNewBlocks.Should().BeFalse();
        Block newBlock = Build.A.Block.WithNumber(3).TestObject;
        AddBlockResult result = blockTree.Insert(newBlock);
        result.Should().Be(AddBlockResult.CannotAccept);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_skip_blocked_tree()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.CanAcceptNewBlocks.Should().BeTrue();
        blockTree.BlockAcceptingNewBlocks();
        blockTree.CanAcceptNewBlocks.Should().BeFalse();
        Block newBlock = Build.A.Block.WithNumber(3).TestObject;
        AddBlockResult result = blockTree.Insert(newBlock, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks);
        result.Should().Be(AddBlockResult.Added);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_block_and_unblock_adding_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.CanAcceptNewBlocks.Should().BeTrue();
        blockTree.BlockAcceptingNewBlocks();
        blockTree.CanAcceptNewBlocks.Should().BeFalse();
        blockTree.BlockAcceptingNewBlocks();
        blockTree.ReleaseAcceptingNewBlocks();
        blockTree.CanAcceptNewBlocks.Should().BeFalse();
        blockTree.ReleaseAcceptingNewBlocks();
        blockTree.CanAcceptNewBlocks.Should().BeTrue();
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(10, false, 10000000ul)]
    [TestCase(4, false, 4000000ul)]
    [TestCase(10, true, 10000000ul)]
    public void Recovers_total_difficulty(int chainLength, bool deleteAllLevels, ulong expectedTotalDifficulty)
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength);
        BlockTree blockTree = blockTreeBuilder.TestObject;
        int chainLeft = deleteAllLevels ? 0 : 1;
        for (int i = chainLength - 1; i >= chainLeft; i--)
        {
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

        blockTree.FindBlock(blockTree.Head!.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should()
            .Be(new UInt256(expectedTotalDifficulty));

        for (int i = chainLength - 1; i >= 0; i--)
        {
            ChainLevelInfo? level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);

            level.Should().NotBeNull();
            level!.BlockInfos.Should().HaveCount(1);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Visitor_can_block_adding_blocks()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ManualResetEvent manualResetEvent = new(false);
        Task acceptTask = blockTree.Accept(new TestBlockTreeVisitor(manualResetEvent), CancellationToken.None);
        blockTree.CanAcceptNewBlocks.Should().BeFalse();
        manualResetEvent.Set();
        await acceptTask;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task SuggestBlockAsync_should_wait_for_blockTree_unlock()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        blockTree.BlockAcceptingNewBlocks();
        ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
        suggest.IsCompleted.Should().Be(false);
        blockTree.ReleaseAcceptingNewBlocks();
        await suggest;
        suggest.IsCompleted.Should().Be(true);
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
        suggest.IsCompleted.Should().Be(false);
        blockTree.ReleaseAcceptingNewBlocks();      // 1st release - 2 blockades left
        suggest.IsCompleted.Should().Be(false);
        blockTree.ReleaseAcceptingNewBlocks();      // 2nd release - 1 blockade left
        suggest.IsCompleted.Should().Be(false);
        blockTree.BlockAcceptingNewBlocks();        // 1 more blockade - 2 blockades left
        suggest.IsCompleted.Should().Be(false);
        blockTree.ReleaseAcceptingNewBlocks();      // release - 1 blockade left
        suggest.IsCompleted.Should().Be(false);
        blockTree.ReleaseAcceptingNewBlocks();      // 3rd release - access unlocked
        await suggest;
        suggest.IsCompleted.Should().Be(true);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task SuggestBlockAsync_works_well_when_there_are_no_blockades()
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
        ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
        await suggest;
        suggest.IsCompleted.Should().Be(true);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void SuggestBlock_should_work_with_zero_difficulty()
    {
        Block genesisWithZeroDifficulty = Build.A.Block.WithDifficulty(0).WithNumber(0).TestObject;
        CustomSpecProvider specProvider = new(((ForkActivation)0, GrayGlacier.Instance));
        specProvider.UpdateMergeTransitionInfo(null, 0);
        BlockTree blockTree = Build.A.BlockTree(genesisWithZeroDifficulty, specProvider).OfChainLength(1).TestObject;

        Block block = Build.A.Block.WithDifficulty(0).WithParent(genesisWithZeroDifficulty).TestObject;
        blockTree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
        blockTree.SuggestBlock(Build.A.Block.WithParent(block).WithDifficulty(0).TestObject).Should().Be(AddBlockResult.Added);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockAddedToMain_should_have_updated_Head()
    {
        BlockTree blockTree = BuildBlockTree();
        Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        AddToMain(blockTree, block0);

        long blockAddedToMainHeadNumber = 0;
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
        findFunction(blockTree, invalidBlock.Hash, lookupOptions).Should().Be(foundInvalid ? invalidBlock.Header : null);
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
                Bloom = Core.Bloom.Empty
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
            });

            tree.SuggestBlock(genesis);
            tree.Genesis.Should().NotBeNull();

            tree.UpdateMainChain(ImmutableList.Create(genesis), wereProcessed);

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

            tree.Genesis.Should().NotBeNull();
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
                .WithTotalDifficulty((long)(currentHeader.TotalDifficulty + 1)!)
                .WithParent(currentHeader)
                .TestObject;
            batch.Add(currentHeader);
        }

        blockTree.BulkInsertHeader(batch);

        for (int i = 1; i < 101; i++)
        {
            blockTree.FindHeader(i, BlockTreeLookupOptions.None).Should().NotBeNull();
        }
    }

    private class TestBlockTreeVisitor(ManualResetEvent manualResetEvent) : IBlockTreeVisitor
    {
        private readonly ManualResetEvent _manualResetEvent = manualResetEvent;
        private bool _wait = true;

        public bool PreventsAcceptingNewBlocks => true;
        public long StartLevelInclusive => 0;
        public long EndLevelExclusive => 3;
        public async Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            if (_wait)
            {
                await _manualResetEvent.WaitOneAsync(cancellationToken);
                _wait = false;
            }

            return LevelVisitOutcome.None;
        }

        public Task<bool> VisitMissing(Hash256 hash, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            return Task.FromResult(HeaderVisitOutcome.None);
        }

        public Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
        {
            return Task.FromResult(BlockVisitOutcome.None);
        }

        public Task<LevelVisitOutcome> VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
        {
            return Task.FromResult(LevelVisitOutcome.None);
        }
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
        blockTree.SyncPivot.Should().Be((999, Hash256.Zero));
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
        blockTree.SyncPivot.Should().Be((1000, TestItem.KeccakA));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_UpdateMainBranch_UpdateSyncPivot_ToLowestPersistedHeader()
    {
        long pivotNumber = 3L;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));

        Block block = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);

        for (long i = 1; i <= 5; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1L).WithParent(block).TestObject;
            tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
            tree.UpdateMainChain(block);
            tree.ForkChoiceUpdated(block.Hash, block.Hash);
            tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));
        }

        tree.BestPersistedState = 5;
        BlockHeader persistedStateHeader = tree.FindHeader(tree.BestPersistedState.Value, BlockTreeLookupOptions.RequireCanonical)!;

        for (long i = 6; i < 10; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1L).WithParent(block).TestObject;
            tree.SuggestBlock(block);
            tree.UpdateMainChain(block);
            tree.ForkChoiceUpdated(block.Hash, block.Hash);
            tree.SyncPivot.Should().Be((persistedStateHeader.Number, persistedStateHeader.Hash!));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_ForkChoiceUpdated_UpdateSyncPivot_ToFinalizedHeader_BeforePersistedState()
    {
        long pivotNumber = 3L;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));

        Block block = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);

        for (long i = 1; i <= 10; i++)
        {
            block = Build.A.Block.WithTotalDifficulty(1L).WithParent(block).TestObject;
            tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
            tree.UpdateMainChain(block);
            tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));
        }

        tree.BestPersistedState = 7;
        BlockHeader persistedStateHeader = tree.FindHeader(tree.BestPersistedState.Value, BlockTreeLookupOptions.RequireCanonical)!;

        for (long i = 4; i < 10; i++)
        {
            BlockHeader header = tree.FindHeader(i, BlockTreeLookupOptions.RequireCanonical)!;
            tree.ForkChoiceUpdated(header.Hash, header.Hash);
            if (header.Number < persistedStateHeader.Number)
            {
                tree.SyncPivot.Should().Be((header.Number, header.Hash!));
            }
            else
            {
                tree.SyncPivot.Should().Be((persistedStateHeader.Number, persistedStateHeader.Hash!));
            }
        }
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void On_UpdateMainBranch_UpdateSyncPivot_ToHeaderUnderReorgDepth()
    {
        long pivotNumber = 3L;

        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = pivotNumber,
            PivotHash = TestItem.KeccakA.ToString(),
        };

        BlockTree tree = Build.A.BlockTree()
            .WithSyncConfig(syncConfig)
            .TestObject;

        tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));

        Block block = Build.A.Block.Genesis.TestObject;
        tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);

        for (long i = 1; i <= 5; i++)
        {
            block = Build.A.Block
                .WithParent(block)
                .WithDifficulty(1L)
                .WithTotalDifficulty(block.TotalDifficulty + 1)
                .TestObject;
            tree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
            tree.UpdateMainChain(block);
            tree.SyncPivot.Should().Be((pivotNumber, TestItem.KeccakA));
        }

        for (long i = 6; i < 100; i++)
        {
            block = Build.A.Block
                .WithParent(block)
                .WithDifficulty(1L)
                .WithTotalDifficulty(block.TotalDifficulty + 1)
                .TestObject;
            tree.SuggestBlock(block);
            tree.UpdateMainChain(block);
            tree.BestPersistedState = block.Number;

            if (block.Number > pivotNumber + Reorganization.MaxDepth)
            {
                BlockHeader reorgDepthHeader = tree.FindHeader(block.Number - Reorganization.MaxDepth, BlockTreeLookupOptions.RequireCanonical)!;
                tree.SyncPivot.Should().Be((reorgDepthHeader.Number, reorgDepthHeader.Hash!));
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
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        Block blockC = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 3 }).TestObject;
        blockTree.SuggestBlock(blockC);
        blockTree.UpdateMainChain(new[] { blockC }, true);

        // C (the third sibling, originally at index 2) must now be canonical
        Block? byNumber = blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);
        byNumber.Should().NotBeNull("RequireCanonical lookup must find C");
        byNumber!.Hash.Should().Be(blockC.Hash!, "C is the last canonical, SwapToMain must have moved it to index 0");

        // A and B must not be canonical
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "A must not be canonical after C was set");
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "B must not be canonical after C was set");

        // All three are still findable by hash (non-canonical lookup)
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull("A findable by hash");
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull("B findable by hash");
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull("C findable by hash");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenCalledWithWereProcessedFalse_MarksBlockCanonical()
    {
        // wereProcessed=false is used during sync to set canonical without updating Head.
        // The canonical marker (HasBlockOnMainChain / BlockInfos[0]) must be set regardless.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, wereProcessed: true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);

        // Reorg to B with wereProcessed=false (sync path)
        blockTree.UpdateMainChain(new[] { blockB }, wereProcessed: false);

        // Canonical marker must be updated even without wereProcessed
        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockB.Hash!,
            "B must be canonical at height 1 even when UpdateMainChain was called with wereProcessed=false");

        blockTree.IsMainChain(blockB.Header).Should().BeTrue("B is canonical");
        blockTree.IsMainChain(blockA.Header).Should().BeFalse("A is no longer canonical");
    }

    [TestCase(1, false, TestName = "SingleDescendant")]
    [TestCase(3, false, TestName = "MultipleDescendants")]
    [TestCase(3, true, TestName = "MultipleDescendantsWithGap")]
    [MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenBeaconSyncMarksThenReorgsToSibling_ClearsStaleMarkers(int descendantCount, bool simulateGap)
    {
        // Beacon sync marks N descendants canonical (wereProcessed=false, Head stays stale at H=1).
        // FCU reorgs to sibling at the same height. All stale markers must be cleared.
        // When simulateGap=true, a concurrent MoveToMain clears one intermediate marker,
        // creating a gap that the bounded scan must handle.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block headBlock = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xAA]).TestObject;
        blockTree.SuggestBlock(headBlock);

        Block[] descendants = BuildAndSuggestChain(blockTree, headBlock, descendantCount);

        // FCU sets Head to headBlock at H=1
        blockTree.UpdateMainChain(new[] { headBlock }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.Head!.Hash.Should().Be(headBlock.Hash!);

        // Beacon sync: mark descendants canonical without advancing Head
        foreach (Block d in descendants)
        {
            blockTree.UpdateMainChain(new[] { d }, wereProcessed: false);
        }

        blockTree.Head!.Number.Should().Be(1, "Head must stay at H=1 — wereProcessed=false");
        foreach (Block d in descendants)
        {
            blockTree.IsMainChain(d.Header).Should().BeTrue($"precondition: block at H={d.Number} canonical via beacon sync");
        }

        if (simulateGap && descendantCount >= 3)
        {
            // Simulate race: concurrent MoveToMain clears middle marker, creating a gap
            ChainLevelInfo? gapLevel = blockTree.FindLevel(descendants[1].Number);
            gapLevel!.HasBlockOnMainChain = false;
            blockTree.IsMainChain(descendants[1].Header).Should().BeFalse("precondition: gap exists");
        }

        // FCU reorg to sibling at H=1
        Block sibling = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xBB }).TestObject;
        blockTree.SuggestBlock(sibling);
        blockTree.UpdateMainChain(new[] { sibling }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(sibling.Hash!);
        blockTree.IsMainChain(sibling.Header).Should().BeTrue("sibling must be canonical");
        foreach (Block d in descendants)
        {
            blockTree.IsMainChain(d.Header).Should().BeFalse($"block at H={d.Number} must be de-canonicalized after reorg");
        }

        // FindCanonicalBlockInfo must return null for all orphaned heights
        for (int h = 2; h <= descendantCount + 1; h++)
        {
            blockTree.FindCanonicalBlockInfo(h).Should().BeNull($"H={h} must return null — orphaned after reorg");
        }

        // Canonical lookup at H=1 must return sibling
        BlockInfo? infoAt1 = blockTree.FindCanonicalBlockInfo(1);
        infoAt1.Should().NotBeNull();
        infoAt1!.BlockHash.Should().Be(sibling.Hash!, "H=1 must return sibling's hash");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenFcuToAncestorWithStaleBeaconSyncedDescendants_ClearsAll()
    {
        // ePBS scenario: FCU can reorg to an ancestor (not just a sibling at the same height).
        // If head is stale because beacon sync marked descendants canonical without updating Head,
        // a subsequent FCU to an ancestor must clear ALL canonical markers above the ancestor —
        // including beacon-synced blocks above the stale head that the IF branch cannot reach.
        //
        // Scenario:
        //   genesis → b1(H=1) → b2(H=2) → b3(H=3) → b4(H=4)
        //
        //   UpdateMainChain([b1], wereProcessed: true)   — FCU(b1): head = b1 at H=1.
        //   UpdateMainChain([b2], wereProcessed: false)  — beacon sync: b2 canonical, head stays at b1.
        //   UpdateMainChain([b3], wereProcessed: false)  — beacon sync: b3 canonical, head stays at b1.
        //   UpdateMainChain([b4], wereProcessed: false)  — beacon sync: b4 canonical, head stays at b1.
        //   UpdateMainChain([genesis], wereProcessed: true) — ePBS FCU to ancestor at H=0:
        //     previousHeadNumber(1) > lastNumber(0) → IF branch clears H=1 only.
        //     b2, b3, b4 are NOT cleared — they are above the stale head and invisible to the IF branch.
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 4);

        // FCU(b1): head = b1 at H=1.
        blockTree.UpdateMainChain(new[] { chain[0] }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Beacon sync: b2, b3, b4 marked canonical without updating Head.
        for (int i = 1; i < chain.Length; i++)
        {
            blockTree.UpdateMainChain(new[] { chain[i] }, wereProcessed: false);
        }

        // Preconditions: head stale at b1, b2-b4 canonical via beacon sync.
        blockTree.Head!.Hash.Should().Be(chain[0].Hash!, "precondition: head stale at b1");
        blockTree.FindBlock(chain[1].Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b2 beacon-synced canonical");
        blockTree.FindBlock(chain[3].Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b4 beacon-synced canonical");

        // ePBS FCU to ancestor: reorg back to genesis at H=0.
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.FindBlock(genesis.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("genesis must be canonical");
        foreach (Block b in chain)
        {
            blockTree.FindBlock(b.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull($"b{b.Number} must be de-canonicalized");
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
        blockTree.UpdateMainChain(new[] { head }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Sync marks descendants canonical without updating Head
        foreach (Block d in descendants)
        {
            blockTree.UpdateMainChain(new[] { d }, wereProcessed: false);
        }

        blockTree.HealCanonicalChain(head.Hash!, maxBlockDepth: 10);

        foreach (Block d in descendants)
        {
            blockTree.FindBlock(d.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull($"H={d.Number} stale marker must be cleared");
        }

        blockTree.FindBlock(head.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("head must remain canonical");
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
        blockTree.UpdateMainChain(new[] { blockA }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.UpdateMainChain(new[] { blockB }, wereProcessed: false); // B wrongly becomes canonical

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(blockB.Hash!, "precondition: B is wrongly canonical");

        blockTree.HealCanonicalChain(blockA.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(blockA.Hash!, "A must be canonical after heal");
        blockTree.IsMainChain(blockB.Header).Should().BeFalse("B must not be canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenChainIsAlreadyConsistent_MakesNoChanges()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis();

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 2);
        blockTree.UpdateMainChain(chain, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.HealCanonicalChain(chain[1].Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(chain[1].Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b2 must remain canonical");
        blockTree.FindBlock(chain[0].Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b1 must remain canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_WhenStartHashIsUnknown_DoesNothing()
    {
        (BlockTree blockTree, _) = BuildBlockTreeWithGenesis();

        // Should not throw — unknown hash is treated as a no-op.
        blockTree.Invoking(bt => bt.HealCanonicalChain(TestItem.KeccakA, maxBlockDepth: 10)).Should().NotThrow();
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
        blockTree.UpdateMainChain(new[] { blockA }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.UpdateMainChain(new[] { blockC }, wereProcessed: false); // sync: C canonical at H=2, head stays A

        // Preconditions: A canonical at H=1, C stale-canonical at H=2, B suggested but not canonical
        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(blockA.Hash!, "precondition: A is canonical at H=1");
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().NotBeNull("precondition: C is stale-canonical at H=2");

        // Heal from B — the CL says B is the correct head.
        // Must both: clear C at H=2 (upward scan) and swap B to index 0 at H=1 (downward walk).
        blockTree.HealCanonicalChain(blockB.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(blockB.Hash!, "B must be canonical at H=1 after heal");
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().BeNull("C must not be canonical — it was orphaned when B replaced A");
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
        blockTree.UpdateMainChain(new[] { b1 }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.UpdateMainChain(new[] { b2 }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.UpdateMainChain(new[] { b1Alt }, wereProcessed: false); // breaks H=1

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(b1Alt.Hash!, "precondition: H=1 is broken");

        // Heal from b2 with depth=0: only checks b2, does not reach H=1
        blockTree.HealCanonicalChain(b2.Hash!, maxBlockDepth: 0);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash
            .Should().Be(b1Alt.Hash!, "H=1 must remain broken — it is beyond maxBlockDepth");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenBeaconSyncAndFcuCycleRepeatedTwice_ClearsStaleMarkersEachRound()
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
        blockTree.UpdateMainChain(new[] { head }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Round 1 — beacon sync marks two descendants of head canonical
        Block[] desc1 = BuildAndSuggestChain(blockTree, head, 2);
        foreach (Block d in desc1)
        {
            blockTree.UpdateMainChain(new[] { d }, wereProcessed: false);
        }

        blockTree.Head!.Hash.Should().Be(head.Hash!, "precondition: head stale at H=1 after round-1 beacon sync");
        foreach (Block d in desc1)
        {
            blockTree.IsMainChain(d.Header).Should().BeTrue($"precondition: round-1 desc at H={d.Number} canonical via beacon sync");
        }

        // Round 1 FCU — reorg to sibling1 at H=1
        Block sibling1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xBB]).TestObject;
        blockTree.SuggestBlock(sibling1);
        blockTree.UpdateMainChain(new[] { sibling1 }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(sibling1.Hash!, "after round-1 FCU head must be sibling1");
        foreach (Block d in desc1)
        {
            blockTree.IsMainChain(d.Header).Should().BeFalse($"round-1 stale marker at H={d.Number} must be cleared after FCU to sibling1");
        }

        // Round 2 — beacon sync marks two descendants of sibling1 canonical
        Block[] desc2 = BuildAndSuggestChain(blockTree, sibling1, 2);
        foreach (Block d in desc2)
        {
            blockTree.UpdateMainChain(new[] { d }, wereProcessed: false);
        }

        blockTree.Head!.Hash.Should().Be(sibling1.Hash!, "precondition: head stale at sibling1 after round-2 beacon sync");
        foreach (Block d in desc2)
        {
            blockTree.IsMainChain(d.Header).Should().BeTrue($"precondition: round-2 desc at H={d.Number} canonical via beacon sync");
        }

        // Round 2 FCU — reorg to sibling2 at H=1
        Block sibling2 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xCC]).TestObject;
        blockTree.SuggestBlock(sibling2);
        blockTree.UpdateMainChain(new[] { sibling2 }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(sibling2.Hash!, "after round-2 FCU head must be sibling2");
        foreach (Block d in desc2)
        {
            blockTree.IsMainChain(d.Header).Should().BeFalse($"round-2 stale marker at H={d.Number} must be cleared after FCU to sibling2");
        }

        // Sibling2 is canonical at H=1; head, sibling1 are orphaned
        blockTree.IsMainChain(sibling2.Header).Should().BeTrue("sibling2 must be canonical at H=1");
        blockTree.IsMainChain(head.Header).Should().BeFalse("original head must be orphaned");
        blockTree.IsMainChain(sibling1.Header).Should().BeFalse("sibling1 must be orphaned");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenForwardProcessingWithBeaconSyncedDescendants_DoesNotClearMarkers()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block[] chain = BuildAndSuggestChain(blockTree, genesis, 4);
        blockTree.UpdateMainChain(new[] { chain[0] }, wereProcessed: true, forceUpdateHeadBlock: true);
        for (int i = 1; i < chain.Length; i++)
            blockTree.UpdateMainChain(new[] { chain[i] }, wereProcessed: false);

        // Forward processing H=2 (forceUpdateHeadBlock: false) must not clear H=3, H=4
        blockTree.UpdateMainChain(new[] { chain[1] }, wereProcessed: true, forceUpdateHeadBlock: false);

        blockTree.IsMainChain(chain[2].Header).Should().BeTrue("H=3 marker must survive");
        blockTree.IsMainChain(chain[3].Header).Should().BeTrue("H=4 marker must survive");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_WhenFcuForwardReorgToLongerChain_ClearsStaleMarkersAboveNewHead()
    {
        (BlockTree blockTree, Block genesis) = BuildBlockTreeWithGenesis(forceUpdateHead: true);

        Block[] chainA = BuildAndSuggestChain(blockTree, genesis, 4);
        blockTree.UpdateMainChain(new[] { chainA[0] }, wereProcessed: true, forceUpdateHeadBlock: true);
        for (int i = 1; i < chainA.Length; i++)
            blockTree.UpdateMainChain(new[] { chainA[i] }, wereProcessed: false);

        // FCU to chain B at H=3 (forceUpdateHeadBlock: true) must clear A4
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData([0xBB]).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).WithExtraData([0xBB]).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).WithExtraData([0xBB]).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.SuggestBlock(b3);
        blockTree.UpdateMainChain(new[] { b1, b2, b3 }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.IsMainChain(chainA[3].Header).Should().BeFalse("A4 stale marker must be cleared");
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
        blockTree.UpdateMainChain(new[] { genesis }, true);

        // Old chain: genesis → A1 → A2 (head at height 2)
        Block a1 = Build.A.Block.WithNumber(1).WithDifficulty(0).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a1);
        Block a2 = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(a1).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a2);
        blockTree.UpdateMainChain(new[] { a1, a2 }, true);

        // Reorg: genesis → B1 (head drops from height 2 to height 1, different block)
        Block b1 = Build.A.Block.WithNumber(1).WithDifficulty(0).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.UpdateMainChain(new[] { b1 }, true);

        // Height 2 was orphaned: must return null, not the stale A2
        blockTree.FindBlock(2, BlockTreeLookupOptions.None).Should().BeNull(
            "orphaned height 2 must return null after reorg in PoS — not the stale A2 block");

        // Height 1 must return the new canonical B1
        blockTree.FindBlock(1, BlockTreeLookupOptions.None)!.Hash.Should().Be(b1.Hash!,
            "height 1 must return B1 after reorg");
    }

}
