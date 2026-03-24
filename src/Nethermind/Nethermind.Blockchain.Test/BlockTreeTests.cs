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
        MemDb blockInfosDb = new MemDb();
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
        TestMemDb blocksInfosDb = new TestMemDb();

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

        BlockStore blockStore = new BlockStore(new MemDb());
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

        Hash256 dec = new Hash256(blockInfosDb.Get(Keccak.Zero)!);
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
        ((IBlockFinder)blockTree).FindPendingHeader().Should().BeSameAs(block0.Header);
        ((IBlockFinder)blockTree).FindPendingBlock().Should().BeSameAs(block0);
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

        MemDb blockInfosDb = new MemDb();
        MemDb headersDb = new MemDb();
        MemDb blockDb = new MemDb();

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
        ManualResetEvent manualResetEvent = new ManualResetEvent(false);
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
        using ArrayPoolList<BlockHeader> batch = new ArrayPoolList<BlockHeader>(1);
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

    private class TestBlockTreeVisitor : IBlockTreeVisitor
    {
        private readonly ManualResetEvent _manualResetEvent;
        private bool _wait = true;

        public TestBlockTreeVisitor(ManualResetEvent manualResetEvent)
        {
            _manualResetEvent = manualResetEvent;
        }

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
        SyncConfig syncConfig = new SyncConfig()
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
        SyncConfig syncConfig = new SyncConfig()
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
    public void FindBlock_by_number_after_reorg_returns_new_canonical_with_and_without_RequireCanonical()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        // Block A at height 1 — initially canonical
        Block blockA = Build.A.Block
            .WithNumber(1)
            .WithParent(genesis)
            .WithExtraData(new byte[] { 1 })
            .TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        // Block B at height 1 — same parent, different block (reorg)
        Block blockB = Build.A.Block
            .WithNumber(1)
            .WithParent(genesis)
            .WithExtraData(new byte[] { 2 })
            .TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // Without RequireCanonical (old BlockParameter(long) behavior)
        Block? withoutCanonical = blockTree.FindBlock(1, BlockTreeLookupOptions.None);

        // With RequireCanonical (new BlockParameter(long) behavior after RequireCanonical-by-number behavior change)
        Block? withCanonical = blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);

        withoutCanonical.Should().NotBeNull();
        withCanonical.Should().NotBeNull();

        withoutCanonical!.Hash.Should().Be(blockB.Hash!,
            "FindBlock without RequireCanonical must return the current canonical block B after reorg");
        withCanonical!.Hash.Should().Be(blockB.Hash!,
            "FindBlock with RequireCanonical must return the current canonical block B after reorg, not the old canonical A");

        withCanonical.Hash.Should().Be(withoutCanonical.Hash,
            "RequireCanonical and non-canonical number lookups must agree on which block is canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_after_double_reorg_A_to_B_back_to_A_returns_A()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockB);

        // A canonical → reorg to B → reorg back to A
        blockTree.UpdateMainChain(new[] { blockA }, true);
        blockTree.UpdateMainChain(new[] { blockB }, true);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block? withoutCanonical = blockTree.FindBlock(1, BlockTreeLookupOptions.None);
        Block? withCanonical = blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);

        withoutCanonical!.Hash.Should().Be(blockA.Hash!, "after reorg back to A, None lookup must return A");
        withCanonical!.Hash.Should().Be(blockA.Hash!, "after reorg back to A, RequireCanonical lookup must return A");
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "B must not be canonical after reorg back to A");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_after_multi_block_reorg_both_heights_updated()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        // Original chain: A1 → A2
        Block a1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block a2 = Build.A.Block.WithNumber(2).WithParent(a1).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(a1);
        blockTree.SuggestBlock(a2);
        blockTree.UpdateMainChain(new[] { a1, a2 }, true);

        // Reorg chain: B1 → B2
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 3 }).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).WithExtraData(new byte[] { 4 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.UpdateMainChain(new[] { b1, b2 }, true);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(b1.Hash!,
            "height 1 must be B1 after reorg");
        blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(b2.Hash!,
            "height 2 must be B2 after reorg");

        blockTree.FindBlock(a1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("A1 must not be canonical");
        blockTree.FindBlock(a2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("A2 must not be canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_after_reorg_to_lower_height_unmarks_higher_levels()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        // Chain: A1 → A2 (head at height 2)
        Block a1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block a2 = Build.A.Block.WithNumber(2).WithParent(a1).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(a1);
        blockTree.SuggestBlock(a2);
        blockTree.UpdateMainChain(new[] { a1, a2 }, true);

        // Reorg to B1 at height 1 (lower than current head)
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 3 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.UpdateMainChain(new[] { b1 }, true);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(b1.Hash!,
            "height 1 must be B1 after reorg to lower height");

        // Height 2 must be unmarked — no canonical block there anymore
        blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "height 2 must be unmarked after reorging to height 1");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_canonical_and_non_canonical_siblings_are_both_accessible_by_hash()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockA }, true);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // B is canonical — accessible both by hash and by number
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.None)!.Hash.Should().Be(blockB.Hash!);
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockB.Hash!);

        // A is not canonical — accessible by hash without RequireCanonical, null with RequireCanonical
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.None)!.Hash.Should().Be(blockA.Hash!,
            "non-canonical block A must still be accessible by hash without RequireCanonical");
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "non-canonical block A must not be returned when RequireCanonical is set");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_UpdateMainChain_called_twice_for_same_block_is_idempotent()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);
        blockTree.UpdateMainChain(new[] { blockA }, true); // second call — must not corrupt state

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockA.Hash!,
            "calling UpdateMainChain twice for the same block must not corrupt canonical state");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_canonical_state_consistent_after_reorg_to_fork_with_different_parent()
    {
        // Scenario matching the Gnosis node bug:
        // P (height 1) → A (height 2, 20 txs) → canonical
        // B (height 2, 32 txs, same parent P) → FCU makes B canonical
        // C (height 3, child of A) → FCU makes A canonical again at height 2
        //
        // After this, A is correctly canonical at height 2 from Nethermind's perspective.
        // Both RequireCanonical and None lookups must agree.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block p = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0 }).TestObject;
        blockTree.SuggestBlock(p);
        blockTree.UpdateMainChain(new[] { p }, true);

        // A at height 2 — initially canonical (20 txs equivalent)
        Block blockA = Build.A.Block.WithNumber(2).WithParent(p).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        // B at height 2 — sibling of A (32 txs equivalent), becomes canonical via reorg
        Block blockB = Build.A.Block.WithNumber(2).WithParent(p).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // C at height 3 — child of A (not B), FCU to C re-canonicalizes A
        Block blockC = Build.A.Block.WithNumber(3).WithParent(blockA).WithExtraData(new byte[] { 3 }).TestObject;
        blockTree.SuggestBlock(blockC);
        blockTree.UpdateMainChain(new[] { blockA, blockC }, true);

        // A must now be canonical at height 2 (FCU to C forced A back)
        Block? byNumberNoCanonical = blockTree.FindBlock(2, BlockTreeLookupOptions.None);
        Block? byNumberWithCanonical = blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical);

        byNumberNoCanonical!.Hash.Should().Be(blockA.Hash!, "height 2 canonical must be A after FCU to C");
        byNumberWithCanonical!.Hash.Should().Be(blockA.Hash!, "RequireCanonical lookup at height 2 must return A");

        // Both lookups must agree
        byNumberNoCanonical.Hash.Should().Be(byNumberWithCanonical!.Hash,
            "None and RequireCanonical lookups must return the same block");

        // B is no longer canonical
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "B must not be canonical after FCU re-canonicalized A");

        // C is canonical at height 3
        blockTree.FindBlock(3, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockC.Hash!,
            "C must be canonical at height 3");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_three_siblings_third_becomes_canonical_SwapToMain_index_greater_than_one()
    {
        // Exercises SwapToMain with index > 1 (three blocks at the same height,
        // the third one — index=2 — is made canonical last).
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

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
    public void FindBlock_canonical_state_survives_BlockTree_reload_from_same_db()
    {
        // Verifies that the canonical marker (HasBlockOnMainChain / BlockInfos[0]) is
        // persisted to the DB and correctly restored when a fresh BlockTree is constructed
        // over the same DB instances — i.e. a node restart preserves canonical state.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // B is canonical before reload
        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockB.Hash!,
            "B must be canonical before reload");

        // Reload: new BlockTree over the same DB instances (simulates node restart)
        BlockTree reloadedTree = Build.A.BlockTree()
            .WithBlocksDb(_blocksDb)
            .WithHeadersDb(_headersDb)
            .WithBlockInfoDb(_blocksInfosDb)
            .WithoutSettingHead
            .TestObject;

        Block? afterReload = reloadedTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);
        afterReload.Should().NotBeNull("canonical block must be findable after reload");
        afterReload!.Hash.Should().Be(blockB.Hash!,
            "B must still be canonical after BlockTree reload from the same DB");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_gap_in_canonical_chain_height_one_not_marked_when_height_two_is()
    {
        // Gap scenario: UpdateMainChain is called with a block at height 2 (C),
        // but height 1 (B, C's parent) was never made canonical.
        // The canonical chain is therefore inconsistent: height 2 is marked,
        // height 1 is not. This documents the current behavior so that any
        // accidental regression (e.g. silently marking height 1) is caught.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockB);
        // Intentionally NOT calling UpdateMainChain for blockB

        Block blockC = Build.A.Block.WithNumber(2).WithParent(blockB).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockC);
        blockTree.UpdateMainChain(new[] { blockC }, true);

        // C is marked canonical at height 2
        blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockC.Hash!,
            "C must be canonical at height 2");

        // B was never passed to UpdateMainChain, so height 1 has no canonical marker
        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "B must NOT be canonical at height 1 — it was never passed to UpdateMainChain");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void IsMainChain_returns_correct_values_after_reorg()
    {
        // After A→B reorg: IsMainChain(B)=true, IsMainChain(A)=false.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        blockTree.IsMainChain(blockB.Header).Should().BeTrue("B is canonical after reorg");
        blockTree.IsMainChain(blockA.Header).Should().BeFalse("A is no longer canonical after reorg");
        blockTree.IsMainChain(blockB.Hash!).Should().BeTrue("hash-based IsMainChain must agree for B");
        blockTree.IsMainChain(blockA.Hash!).Should().BeFalse("hash-based IsMainChain must agree for A");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_by_hash_with_RequireCanonical_returns_null_for_non_canonical_hash()
    {
        // FindBlock(Hash, RequireCanonical) uses a separate code path from number-based lookup.
        // It must return null when the hash belongs to a non-canonical block.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // B canonical: hash lookup with RequireCanonical must succeed
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull(
            "B is canonical — hash lookup with RequireCanonical must return it");

        // A non-canonical: hash lookup with RequireCanonical must return null
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "A is not canonical — hash lookup with RequireCanonical must return null");

        // Without RequireCanonical both are still findable
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull("A findable without RequireCanonical");
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull("B findable without RequireCanonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_by_number_without_RequireCanonical_uses_best_difficulty_fallback_when_no_canonical_marked()
    {
        // When HasBlockOnMainChain=false (no UpdateMainChain called), GetBlockHashOnMainOrBestDifficultyHash
        // falls back to the best-TD loop using >=, so the last SuggestBlock call wins (TD=0 in PoS).
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        // Suggest two siblings but do NOT call UpdateMainChain for either
        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);

        // Neither is canonical — RequireCanonical must return null
        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "no canonical block at height 1 — RequireCanonical must return null");

        // Without RequireCanonical the best-difficulty fallback returns a block.
        // With TD=0 and >= comparison, the last SuggestBlock (B) wins.
        Block? fallback = blockTree.FindBlock(1, BlockTreeLookupOptions.None);
        fallback.Should().NotBeNull("best-difficulty fallback must return a block when no canonical is set");
        fallback!.Hash.Should().Be(blockB.Hash!,
            "last SuggestBlock (B) wins the TD=0 >= fallback tie-break");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_UpdateMainChain_with_wereProcessed_false_still_marks_canonical()
    {
        // wereProcessed=false is used during sync to set canonical without updating Head.
        // The canonical marker (HasBlockOnMainChain / BlockInfos[0]) must be set regardless.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

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

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_sync_marks_descendant_canonical_then_reorg_to_sibling_decanonalizes_it()
    {
        // Regression test for the Gnosis canonical-mismatch bug.
        //
        // BlockDownloader calls UpdateMainChain(block, wereProcessed: false) to mark synced
        // blocks canonical without updating Head. When a reorg then arrives at the same height
        // as the stale Head (previousHeadNumber == lastNumber), the old unmark loop
        // (previousHeadNumber > lastNumber) is skipped, leaving the orphaned descendant
        // wrongly canonical so eth_getBlockByNumber returns the wrong block.
        //
        // The fix adds an else-branch that scans upward from lastNumber+1 to clear any
        // stale canonical markers left by the sync path.
        //
        // Scenario:
        //   UpdateMainChain([A], wereProcessed: true)  — FCU(A): head = A at H=1.
        //   UpdateMainChain([C], wereProcessed: false) — sync: C canonical at H=2, head stays at A.
        //   UpdateMainChain([B], wereProcessed: true)  — FCU(B, H=1, sibling of A):
        //     previousHeadNumber(1) == lastNumber(1) → else-branch fires → C at H=2 decanonalized.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        Block blockC = Build.A.Block.WithNumber(2).WithParent(blockA).TestObject;

        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockB);
        blockTree.SuggestBlock(blockC);

        // FCU(A): A becomes head at H=1.
        blockTree.UpdateMainChain(new[] { blockA }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.Head!.Hash.Should().Be(blockA.Hash!, "head must be A");

        // Sync marks C canonical at H=2 without updating Head (BlockDownloader path).
        blockTree.UpdateMainChain(new[] { blockC }, wereProcessed: false);
        blockTree.Head!.Hash.Should().Be(blockA.Hash!, "head must stay at A — wereProcessed=false");
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().NotBeNull("C must be canonical at H=2 after sync marks it");

        // FCU(B): reorg to sibling at the same height as the stale Head.
        // previousHeadNumber(1) == lastNumber(1) → old loop skipped → else-branch must clear C.
        blockTree.UpdateMainChain(new[] { blockB }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(blockB.Hash!, "head must be B after reorg");
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "C must not be canonical — its parent A was replaced by B");
        blockTree.FindBlock(blockB.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull(
            "B must be canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_beacon_sync_multiple_descendants_then_reorg_to_sibling_clears_all()
    {
        // Exact reproduction of the stale canonical markers bug from the Engine API test generator.
        //
        // Beacon sync marks H+1, H+2, H+3 canonical without updating Head (wereProcessed: false).
        // FCU reorgs to a sibling of Head at the SAME height H.
        //   previousHeadNumber == lastNumber → downward unmark skipped entirely.
        //   Upward scan must clear all three orphaned levels.
        //
        // This differs from the single-descendant test above: with multiple levels, the bounded
        // scan (not break-on-first-gap) is critical — a concurrent MoveToMain could create a gap.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Chain: genesis → headBlock(H=1) → d1(H=2) → d2(H=3) → d3(H=4)
        Block headBlock = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xAA }).TestObject;
        Block d1 = Build.A.Block.WithNumber(2).WithParent(headBlock).TestObject;
        Block d2 = Build.A.Block.WithNumber(3).WithParent(d1).TestObject;
        Block d3 = Build.A.Block.WithNumber(4).WithParent(d2).TestObject;

        blockTree.SuggestBlock(headBlock);
        blockTree.SuggestBlock(d1);
        blockTree.SuggestBlock(d2);
        blockTree.SuggestBlock(d3);

        // FCU sets Head to headBlock at H=1
        blockTree.UpdateMainChain(new[] { headBlock }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.Head!.Hash.Should().Be(headBlock.Hash!);

        // Beacon sync: d1, d2, d3 marked canonical without advancing Head
        blockTree.UpdateMainChain(new[] { d1 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { d2 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { d3 }, wereProcessed: false);

        // Step 2: verify stale Head
        blockTree.Head!.Number.Should().Be(1, "Head must stay at H=1 — wereProcessed=false");
        blockTree.IsMainChain(d1.Header).Should().BeTrue("precondition: d1 canonical via beacon sync");
        blockTree.IsMainChain(d2.Header).Should().BeTrue("precondition: d2 canonical via beacon sync");
        blockTree.IsMainChain(d3.Header).Should().BeTrue("precondition: d3 canonical via beacon sync");

        // Step 3: FCU reorg to sibling at same height H=1
        Block sibling = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xBB }).TestObject;
        blockTree.SuggestBlock(sibling);
        blockTree.UpdateMainChain(new[] { sibling }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Step 4: verify reorg correctness
        blockTree.Head!.Number.Should().Be(1);
        blockTree.Head!.Hash.Should().Be(sibling.Hash!);
        blockTree.IsMainChain(sibling.Header).Should().BeTrue("sibling must be canonical");
        blockTree.IsMainChain(d1.Header).Should().BeFalse("d1 must be de-canonicalized after reorg");
        blockTree.IsMainChain(d2.Header).Should().BeFalse("d2 must be de-canonicalized after reorg");
        blockTree.IsMainChain(d3.Header).Should().BeFalse("d3 must be de-canonicalized after reorg");

        // Step 5: verify user-visible impact — FindCanonicalBlockInfo must return null
        blockTree.FindCanonicalBlockInfo(2).Should().BeNull("H+1 must return null — orphaned after reorg");
        blockTree.FindCanonicalBlockInfo(3).Should().BeNull("H+2 must return null — orphaned after reorg");
        blockTree.FindCanonicalBlockInfo(4).Should().BeNull("H+3 must return null — orphaned after reorg");

        // Step 6: verify block hashes — canonical lookup returns correct hash at H=1
        BlockInfo? infoAt1 = blockTree.FindCanonicalBlockInfo(1);
        infoAt1.Should().NotBeNull();
        infoAt1!.BlockHash.Should().Be(sibling.Hash!, "H=1 must return sibling's hash, not old headBlock");
        infoAt1.BlockHash.Should().NotBe(headBlock.Hash!, "H=1 must NOT return old headBlock's hash");

        // Orphaned heights must not return old block hashes via canonical lookup
        blockTree.FindCanonicalBlockInfo(2)?.BlockHash.Should().NotBe(d1.Hash!, "H+1 canonical hash must not be d1");
        blockTree.FindCanonicalBlockInfo(3)?.BlockHash.Should().NotBe(d2.Hash!, "H+2 canonical hash must not be d2");
        blockTree.FindCanonicalBlockInfo(4)?.BlockHash.Should().NotBe(d3.Hash!, "H+3 canonical hash must not be d3");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_fcu_to_ancestor_with_stale_head_clears_all_beacon_synced_descendants()
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
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).TestObject;
        Block b4 = Build.A.Block.WithNumber(4).WithParent(b3).TestObject;

        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.SuggestBlock(b3);
        blockTree.SuggestBlock(b4);

        // FCU(b1): head = b1 at H=1.
        blockTree.UpdateMainChain(new[] { b1 }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Beacon sync: b2, b3, b4 marked canonical without updating Head.
        blockTree.UpdateMainChain(new[] { b2 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { b3 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { b4 }, wereProcessed: false);

        // Preconditions: head stale at b1, b2-b4 canonical via beacon sync.
        blockTree.Head!.Hash.Should().Be(b1.Hash!, "precondition: head stale at b1");
        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b2 beacon-synced canonical");
        blockTree.FindBlock(b4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b4 beacon-synced canonical");

        // ePBS FCU to ancestor: reorg back to genesis at H=0.
        // The IF branch (previousHeadNumber=1 > lastNumber=0) clears b1 at H=1.
        // b2, b3, b4 must also be cleared — they are above the stale head.
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.FindBlock(genesis.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("genesis must be canonical");
        blockTree.FindBlock(b1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b1 must be de-canonicalized");
        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b2 must be de-canonicalized — beacon sync stale marker above stale head");
        blockTree.FindBlock(b3.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b3 must be de-canonicalized — beacon sync stale marker above stale head");
        blockTree.FindBlock(b4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b4 must be de-canonicalized — beacon sync stale marker above stale head");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_clears_stale_marker_above_head_left_by_sync()
    {
        // Scenario: sync (wereProcessed=false) marks C canonical at H=2 without updating Head.
        // HealCanonicalChain(head=A) must scan upward and clear C's stale marker.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block blockC = Build.A.Block.WithNumber(2).WithParent(blockA).TestObject;

        blockTree.SuggestBlock(blockA);
        blockTree.SuggestBlock(blockC);

        // FCU: head = A at H=1
        blockTree.UpdateMainChain(new[] { blockA }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Sync marks C canonical at H=2 without updating Head
        blockTree.UpdateMainChain(new[] { blockC }, wereProcessed: false);
        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().NotBeNull("precondition: C is canonical before heal");

        blockTree.HealCanonicalChain(blockA.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(blockC.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().BeNull("C must be decanonalized after heal");
        blockTree.FindBlock(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical)
            .Should().NotBeNull("A must remain canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_fixes_wrong_canonical_block_at_height()
    {
        // Scenario: A and B are siblings at H=1. B was swapped to index 0 by accident
        // (e.g. a stale write), but the real canonical chain goes through A.
        // HealCanonicalChain walking from A must swap A back to index 0.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

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
    public void HealCanonicalChain_does_not_alter_already_consistent_chain()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;

        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.UpdateMainChain(new[] { b1, b2 }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.HealCanonicalChain(b2.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b2 must remain canonical");
        blockTree.FindBlock(b1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b1 must remain canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_does_nothing_for_unknown_start_hash()
    {
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        // Should not throw — unknown hash is treated as a no-op.
        blockTree.Invoking(bt => bt.HealCanonicalChain(TestItem.KeccakA, maxBlockDepth: 10))
            .Should().NotThrow();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_clears_multiple_stale_levels_above_head()
    {
        // Sync left THREE levels above head canonical — heal must clear all of them.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).TestObject;
        Block b4 = Build.A.Block.WithNumber(4).WithParent(b3).TestObject;

        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.SuggestBlock(b3);
        blockTree.SuggestBlock(b4);

        // FCU: head = b1 at H=1
        blockTree.UpdateMainChain(new[] { b1 }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Sync marks b2, b3, b4 canonical without updating Head
        blockTree.UpdateMainChain(new[] { b2 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { b3 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { b4 }, wereProcessed: false);

        blockTree.HealCanonicalChain(b1.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("H=2 stale marker must be cleared");
        blockTree.FindBlock(b3.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("H=3 stale marker must be cleared");
        blockTree.FindBlock(b4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("H=4 stale marker must be cleared");
        blockTree.FindBlock(b1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b1 must remain canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_fixes_both_directions_in_one_pass()
    {
        // Combined scenario: wrong canonical at H=1 AND stale markers at H=2,3 above head.
        // This is the realistic production scenario: FCU moved head to B at H=1,
        // but C (child of A) was left canonical at H=2 by sync, and A is still canonical at H=1
        // when it shouldn't be.
        //
        //   genesis → A(H=1) → C(H=2)   ← sync left C canonical, A at H=1
        //           → B(H=1)             ← heal starts from B, the correct head
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

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
    public void HealCanonicalChain_respects_maxBlockDepth()
    {
        // With maxBlockDepth=1, the walk only checks the start block and one parent.
        // A broken marker two levels below must NOT be repaired.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true);

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
    public void Canonical_chain_walk_every_ancestor_is_IsMainChain_true()
    {
        // Walk from canonical head back to genesis via ParentHash.
        // Every block in the chain must satisfy IsMainChain=true.
        // This catches the Gnosis-style bug where a child is canonical but its parent is not.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.UpdateMainChain(new[] { b1 }, true);

        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;
        blockTree.SuggestBlock(b2);
        blockTree.UpdateMainChain(new[] { b2 }, true);

        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).TestObject;
        blockTree.SuggestBlock(b3);
        blockTree.UpdateMainChain(new[] { b3 }, true);

        // Walk canonical chain from head down to genesis
        BlockHeader? current = blockTree.FindHeader(3, BlockTreeLookupOptions.RequireCanonical);
        current.Should().NotBeNull("canonical head must exist at height 3");

        while (current!.Number > 0)
        {
            blockTree.IsMainChain(current).Should().BeTrue(
                $"block at height {current.Number} (hash {current.Hash}) must be IsMainChain=true");
            current = blockTree.FindHeader(current.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            current.Should().NotBeNull($"parent must exist when walking canonical chain");
        }

        blockTree.IsMainChain(current!).Should().BeTrue("genesis must be IsMainChain=true");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_long_branch_reorg_all_new_heights_canonical_old_height_unmarked()
    {
        // Head at A(1). Reorg to B(1)→C(2)→D(3).
        // After reorg: B, C, D canonical; A unmarked; heights 2 and 3 newly marked.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        // Competing branch: B sibling of A, then C and D on top of B
        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);

        Block blockC = Build.A.Block.WithNumber(2).WithParent(blockB).TestObject;
        blockTree.SuggestBlock(blockC);

        Block blockD = Build.A.Block.WithNumber(3).WithParent(blockC).TestObject;
        blockTree.SuggestBlock(blockD);

        blockTree.UpdateMainChain(new[] { blockB, blockC, blockD }, true);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockB.Hash!, "B canonical at height 1");
        blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockC.Hash!, "C canonical at height 2");
        blockTree.FindBlock(3, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(blockD.Hash!, "D canonical at height 3");
        blockTree.IsMainChain(blockA.Header).Should().BeFalse("A must be unmarked after reorg to longer branch");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_reorg_to_shorter_chain_higher_levels_unmarked()
    {
        // Head at height 3 (A1→A2→A3). Reorg to B1 (height 1 only).
        // Heights 2 and 3 must be unmarked; B1 must be canonical at height 1.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block a1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a1);
        Block a2 = Build.A.Block.WithNumber(2).WithParent(a1).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a2);
        Block a3 = Build.A.Block.WithNumber(3).WithParent(a2).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(a3);
        blockTree.UpdateMainChain(new[] { a1, a2, a3 }, true);

        // Reorg to a competing block at height 1 only
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.UpdateMainChain(new[] { b1 }, true);

        blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical)!.Hash.Should().Be(b1.Hash!, "B1 canonical at height 1");
        blockTree.FindBlock(2, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("height 2 must be unmarked after reorg to shorter chain");
        blockTree.FindBlock(3, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("height 3 must be unmarked after reorg to shorter chain");
        blockTree.IsMainChain(a1.Header).Should().BeFalse("A1 no longer canonical");
        blockTree.IsMainChain(a2.Header).Should().BeFalse("A2 no longer canonical");
        blockTree.IsMainChain(a3.Header).Should().BeFalse("A3 no longer canonical");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindHeader_by_number_with_RequireCanonical_mirrors_FindBlock_behavior()
    {
        // FindHeader(long, RequireCanonical) uses GetBlockHashOnMainOrBestDifficultyHash then
        // checks level.MainChainBlock — same logic as FindBlock but returning only the header.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, true);

        Block blockA = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 1 }).TestObject;
        blockTree.SuggestBlock(blockA);
        blockTree.UpdateMainChain(new[] { blockA }, true);

        Block blockB = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 2 }).TestObject;
        blockTree.SuggestBlock(blockB);
        blockTree.UpdateMainChain(new[] { blockB }, true);

        // B canonical: FindHeader with RequireCanonical must return B's header
        BlockHeader? headerCanonical = blockTree.FindHeader(1, BlockTreeLookupOptions.RequireCanonical);
        headerCanonical.Should().NotBeNull("canonical header must be found at height 1");
        headerCanonical!.Hash.Should().Be(blockB.Hash!, "FindHeader(RequireCanonical) must return B after reorg");

        // A non-canonical: FindHeader(hash, RequireCanonical) must return null
        blockTree.FindHeader(blockA.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull(
            "FindHeader by A's hash with RequireCanonical must return null — A is not canonical");

        // FindHeader and FindBlock must agree on canonical hash
        Block? blockCanonical = blockTree.FindBlock(1, BlockTreeLookupOptions.RequireCanonical);
        blockCanonical!.Hash.Should().Be(headerCanonical.Hash,
            "FindHeader and FindBlock must return the same canonical block at height 1");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlock_by_number_after_reorg_returns_null_not_orphaned_block_in_pos()
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

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_clears_non_contiguous_stale_markers_after_reorg()
    {
        // Reproduces the Engine API reorg bug: a gap in HasBlockOnMainChain markers
        // left by concurrent processing causes a break-on-first-gap scan to miss
        // stale canonical blocks above the gap.
        //
        // Scenario:
        //   genesis → a1(H=1) → a2(H=2) → a3(H=3) → a4(H=4)
        //
        // 1. Mark a1..a4 canonical with wereProcessed=true (simulates building blocks).
        // 2. Manually clear a3's HasBlockOnMainChain to simulate a gap left by a
        //    concurrent MoveToMain from BlockchainProcessor re-marking only a2.
        // 3. Reorg to b1 at H=1 — UpdateMainChain must clear a2 and a4 despite the gap at a3.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        Block a1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xA1 }).TestObject;
        Block a2 = Build.A.Block.WithNumber(2).WithParent(a1).WithExtraData(new byte[] { 0xA2 }).TestObject;
        Block a3 = Build.A.Block.WithNumber(3).WithParent(a2).WithExtraData(new byte[] { 0xA3 }).TestObject;
        Block a4 = Build.A.Block.WithNumber(4).WithParent(a3).WithExtraData(new byte[] { 0xA4 }).TestObject;

        blockTree.SuggestBlock(a1);
        blockTree.SuggestBlock(a2);
        blockTree.SuggestBlock(a3);
        blockTree.SuggestBlock(a4);
        blockTree.UpdateMainChain(new[] { a1, a2, a3, a4 }, wereProcessed: true, forceUpdateHeadBlock: true);

        // Precondition: head at a4, all canonical
        blockTree.Head!.Hash.Should().Be(a4.Hash!);

        // Simulate gap: clear a3's canonical marker (as if concurrent processing left a hole)
        ChainLevelInfo? levelA3 = blockTree.FindLevel(a3.Number);
        levelA3!.HasBlockOnMainChain = false;

        // Reorg to b1 at H=1
        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).WithExtraData(new byte[] { 0xB1 }).TestObject;
        blockTree.SuggestBlock(b1);
        blockTree.UpdateMainChain(new[] { b1 }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(b1.Hash!);
        blockTree.FindBlock(b1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b1 must be canonical");
        blockTree.FindBlock(a2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("a2 must be cleared despite gap at a3");
        blockTree.FindBlock(a3.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("a3 (the gap) must stay non-canonical");
        blockTree.FindBlock(a4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("a4 must be cleared — bounded scan must reach past the gap");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_bounded_scan_clears_stale_markers_when_head_equals_lastNumber()
    {
        // Reproduces the exact FCU reorg scenario from the test generator:
        // Head is at H+1 (same as lastNumber) so the downward unmark doesn't fire,
        // and the upward scan must clear H+2..H+N.
        // With a gap at H+3, the old break-on-first-gap scan would stop at H+3 and miss H+4.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        Block hook = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block sep = Build.A.Block.WithNumber(2).WithParent(hook).WithExtraData(new byte[] { 0x01 }).TestObject;
        Block scenario1 = Build.A.Block.WithNumber(3).WithParent(sep).WithExtraData(new byte[] { 0x02 }).TestObject;
        Block scenario2 = Build.A.Block.WithNumber(4).WithParent(scenario1).WithExtraData(new byte[] { 0x03 }).TestObject;

        blockTree.SuggestBlock(hook);
        blockTree.SuggestBlock(sep);
        blockTree.SuggestBlock(scenario1);
        blockTree.SuggestBlock(scenario2);

        // Build the chain: head at scenario2 (H=4)
        blockTree.UpdateMainChain(new[] { hook, sep, scenario1, scenario2 }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.Head!.Hash.Should().Be(scenario2.Hash!);

        // Simulate stale head: force Head back to hook (H=1) without clearing markers,
        // as if the BCProcessor set Head but canonical markers weren't fully updated
        Block newSep = Build.A.Block.WithNumber(2).WithParent(hook).WithExtraData(new byte[] { 0xFF }).TestObject;
        blockTree.SuggestBlock(newSep);

        // Simulate gap: clear scenario1 marker at H=3
        ChainLevelInfo? level3 = blockTree.FindLevel(scenario1.Number);
        level3!.HasBlockOnMainChain = false;

        // Reorg: FCU sets head to newSep at H=2 from stale head at H=4
        blockTree.UpdateMainChain(new[] { newSep }, wereProcessed: true, forceUpdateHeadBlock: true);

        blockTree.Head!.Hash.Should().Be(newSep.Hash!);
        blockTree.FindBlock(newSep.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("newSep must be canonical");
        blockTree.FindBlock(sep.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("old sep must be de-canonicalized");
        blockTree.FindBlock(scenario1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("scenario1 (gap) must stay non-canonical");
        blockTree.FindBlock(scenario2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("scenario2 must be cleared past the gap at scenario1");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UpdateMainChain_repeated_reorgs_never_leave_stale_markers()
    {
        // Simulates the Engine API test generator's repeated build-and-reorg cycle:
        // Build blocks H+1..H+3 on a hook, then reorg back to a new separator at H+1.
        // Repeat multiple times. No stale markers should ever be visible.
        // Uses PoS spec (TTD=0) so the TD fallback returns null for orphaned heights.
        CustomSpecProvider specProvider = new(((ForkActivation)0, London.Instance))
        {
            TerminalTotalDifficulty = UInt256.Zero
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
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        Block hook = Build.A.Block.WithNumber(1).WithDifficulty(0).WithParent(genesis).TestObject;
        blockTree.SuggestBlock(hook);
        blockTree.UpdateMainChain(new[] { hook }, wereProcessed: true, forceUpdateHeadBlock: true);

        for (int iteration = 0; iteration < 10; iteration++)
        {
            byte tag = (byte)(iteration + 1);

            // Build scenario chain: hook → sep → s1 → s2
            Block sep = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(hook).WithExtraData(new byte[] { tag, 1 }).TestObject;
            Block s1 = Build.A.Block.WithNumber(3).WithDifficulty(0).WithParent(sep).WithExtraData(new byte[] { tag, 2 }).TestObject;
            Block s2 = Build.A.Block.WithNumber(4).WithDifficulty(0).WithParent(s1).WithExtraData(new byte[] { tag, 3 }).TestObject;

            blockTree.SuggestBlock(sep);
            blockTree.SuggestBlock(s1);
            blockTree.SuggestBlock(s2);
            blockTree.UpdateMainChain(new[] { sep, s1, s2 }, wereProcessed: true, forceUpdateHeadBlock: true);
            blockTree.Head!.Number.Should().Be(4);

            // Reorg back: new separator on hook
            Block newSep = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(hook).WithExtraData(new byte[] { tag, 0xFF }).TestObject;
            blockTree.SuggestBlock(newSep);
            blockTree.UpdateMainChain(new[] { newSep }, wereProcessed: true, forceUpdateHeadBlock: true);

            blockTree.Head!.Number.Should().Be(2);
            blockTree.FindBlock(3, BlockTreeLookupOptions.None).Should().BeNull(
                $"iteration {iteration}: height 3 must be null after reorg");
            blockTree.FindBlock(4, BlockTreeLookupOptions.None).Should().BeNull(
                $"iteration {iteration}: height 4 must be null after reorg");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HealCanonicalChain_clears_non_contiguous_stale_markers_above_head()
    {
        // HealCanonicalChain must also use a bounded scan (not break-on-first-gap)
        // when clearing stale markers above head.
        BlockTree blockTree = BuildBlockTree();

        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        blockTree.SuggestBlock(genesis);
        blockTree.UpdateMainChain(new[] { genesis }, wereProcessed: true, forceUpdateHeadBlock: true);

        Block b1 = Build.A.Block.WithNumber(1).WithParent(genesis).TestObject;
        Block b2 = Build.A.Block.WithNumber(2).WithParent(b1).TestObject;
        Block b3 = Build.A.Block.WithNumber(3).WithParent(b2).TestObject;
        Block b4 = Build.A.Block.WithNumber(4).WithParent(b3).TestObject;

        blockTree.SuggestBlock(b1);
        blockTree.SuggestBlock(b2);
        blockTree.SuggestBlock(b3);
        blockTree.SuggestBlock(b4);

        // Set head at b1, but beacon sync marks b2, b4 canonical (with gap at b3)
        blockTree.UpdateMainChain(new[] { b1 }, wereProcessed: true, forceUpdateHeadBlock: true);
        blockTree.UpdateMainChain(new[] { b2 }, wereProcessed: false);
        blockTree.UpdateMainChain(new[] { b4 }, wereProcessed: false);

        // Precondition: b2 canonical, b3 not canonical (gap), b4 canonical
        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b2 canonical");
        blockTree.FindBlock(b3.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("precondition: b3 not canonical (gap)");
        blockTree.FindBlock(b4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("precondition: b4 canonical");

        blockTree.HealCanonicalChain(b1.Hash!, maxBlockDepth: 10);

        blockTree.FindBlock(b1.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().NotBeNull("b1 must remain canonical");
        blockTree.FindBlock(b2.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b2 must be cleared by heal");
        blockTree.FindBlock(b3.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b3 must stay non-canonical");
        blockTree.FindBlock(b4.Hash!, BlockTreeLookupOptions.RequireCanonical).Should().BeNull("b4 must be cleared — heal must scan past gap at b3");
    }
}
