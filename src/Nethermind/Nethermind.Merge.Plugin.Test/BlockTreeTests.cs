//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class BlockTreeTests
{
    private (BlockTree notSyncedTree, BlockTree syncedTree) BuildBlockTrees(
        int notSyncedTreeSize, int syncedTreeSize)
    {
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize);
        BlockTree notSyncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        
        BlockTree syncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);

        Block parent = syncedTree.Head!;
        for (int i = 0; i < syncedTreeSize - notSyncedTreeSize; ++i)
        {
            Block block = Build.A.Block.WithNumber(parent!.Number + 1).WithParent(parent).TestObject;
            AddBlockResult addBlockResult = syncedTree.SuggestBlock(block);
            Assert.AreEqual(AddBlockResult.Added, addBlockResult);

            parent = block;
        }

        return (notSyncedTree, syncedTree);
    }

    [Test]
    public void Can_build_correct_block_tree()
    {
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        
        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
    }

    [Test]
    public void Can_start_insert_pivot_block_with_correct_pointers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions insertOption = BlockTreeInsertOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, insertOption);
        
        Assert.AreEqual(AddBlockResult.Added, insertResult);
        Assert.AreEqual(9, notSyncedTree.BestKnownNumber);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedHeader!.Number);
        Assert.AreEqual(9, notSyncedTree.Head!.Number);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedBody!.Number);
        Assert.AreEqual(14, notSyncedTree.BestKnownBeaconNumber);
        Assert.AreEqual(14, notSyncedTree.BestSuggestedBeaconHeader!.Number);
    }
    
        
    [Test]
    public void Can_insert_beacon_headers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions options = BlockTreeInsertOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, options);
        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, options);
            Assert.AreEqual(insertOutcome, insertResult);
        }
    }
    
    [Test]
    public void Can_fill_beacon_headers_gap()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions options = BlockTreeInsertOptions.BeaconBlockInsert;
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, options);
        
        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, options);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }
        
        for (int i = 10; i <14; ++i)
        {
            Block? block = syncedTree.FindBlock(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.SuggestBlock(block!);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }
        
        Assert.AreEqual(13, notSyncedTree.BestSuggestedBody!.Number);
    }

    public static class BlockTreeTestScenario
        {
            public class ScenarioBuilder
            {
                private BlockTreeBuilder? _syncedTreeBuilder;
                private IChainLevelHelper? _chainLevelHelper;

                public ScenarioBuilder WithBlockTrees(int notSyncedTreeSize, int syncedTreeSize = -1, bool moveBlocksToMainChain = true)
                {
                    NotSyncedTreeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize);
                    NotSyncedTree = new(
                        NotSyncedTreeBuilder.BlocksDb,
                        NotSyncedTreeBuilder.HeadersDb,
                        NotSyncedTreeBuilder.BlockInfoDb,
                        NotSyncedTreeBuilder.MetadataDb,
                        NotSyncedTreeBuilder.ChainLevelInfoRepository,
                        MainnetSpecProvider.Instance,
                        NullBloomStorage.Instance,
                        new SyncConfig(),
                        LimboLogs.Instance);

                    if (syncedTreeSize > 0)
                    {
                        _syncedTreeBuilder = Build.A.BlockTree().OfChainLength(syncedTreeSize);
                        SyncedTree = new(
                            _syncedTreeBuilder.BlocksDb,
                            _syncedTreeBuilder.HeadersDb,
                            _syncedTreeBuilder.BlockInfoDb,
                            _syncedTreeBuilder.MetadataDb,
                            _syncedTreeBuilder.ChainLevelInfoRepository,
                            MainnetSpecProvider.Instance,
                            NullBloomStorage.Instance,
                            new SyncConfig(),
                            LimboLogs.Instance);
                    }

                    _chainLevelHelper = new ChainLevelHelper(NotSyncedTree, new SyncConfig(), LimboLogs.Instance);
                    if (moveBlocksToMainChain)
                        NotSyncedTree.NewBestSuggestedBlock += OnNewBestSuggestedBlock;
                    return this;
                }
                
                private void OnNewBestSuggestedBlock(object? sender, BlockEventArgs e)
                {
                    NotSyncedTree.UpdateMainChain(new[] { e.Block! }, true);
                }

                public ScenarioBuilder InsertBeaconPivot(long num)
                {
                    Block? beaconBlock = SyncedTree.FindBlock(num, BlockTreeLookupOptions.None);
                    AddBlockResult insertResult = NotSyncedTree.Insert(beaconBlock!, true,
                        BlockTreeInsertOptions.BeaconBlockInsert);
                    Assert.AreEqual(AddBlockResult.Added, insertResult);
                    NotSyncedTreeBuilder.MetadataDb.Set(MetadataDbKeys.LowestInsertedBeaconHeaderHash, Rlp.Encode(beaconBlock!.Hash).Bytes);
                    NotSyncedTreeBuilder.MetadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber, Rlp.Encode(beaconBlock.Number ).Bytes);
                    return this;
                }

                public ScenarioBuilder ClearBeaconPivot()
                {
                    NotSyncedTreeBuilder.MetadataDb.Delete(MetadataDbKeys.BeaconSyncPivotNumber);

                    return this;
                }

                public ScenarioBuilder SuggestBlocks(long low, long high)
                {
                    for (long i = low; i <= high; i++)
                    {
                        Block? beaconBlock = SyncedTree!.FindBlock(i, BlockTreeLookupOptions.None);
                        AddBlockResult insertResult = NotSyncedTree!.SuggestBlock(beaconBlock!);
                        Assert.AreEqual(AddBlockResult.Added, insertResult);
                    }
                    return this;
                }
                
                public ScenarioBuilder SuggestBlocksUsingChainLevels(int maxCount = 2)
                {
                    BlockHeader[] headers = _chainLevelHelper!.GetNextHeaders(maxCount);
                    while (headers != null && headers.Length > 0)
                    {
                        for (int i = 0; i < headers.Length; ++i)
                        {
                            Block? beaconBlock = SyncedTree.FindBlock(headers[i].Hash!, BlockTreeLookupOptions.None);
                            beaconBlock.Header.TotalDifficulty = null;
                            AddBlockResult insertResult = NotSyncedTree.SuggestBlock(beaconBlock!, BlockTreeSuggestOptions.ShouldProcess | BlockTreeSuggestOptions.FillBeaconBlock, true);
                            Assert.True(AddBlockResult.Added == insertResult, $"BeaconBlock {beaconBlock!.ToString(Block.Format.FullHashAndNumber)}");
                        }
                        
                        headers = _chainLevelHelper!.GetNextHeaders(maxCount);
                    }
                    
                    return this;
                }

                public enum TotalDifficultyMode
                {
                    Null,
                    Zero,
                    TheSameAsSyncedTree
                }

                public ScenarioBuilder InsertHeaders(long low, long high, TotalDifficultyMode tdMode = TotalDifficultyMode.TheSameAsSyncedTree)
                {
                    BlockTreeInsertOptions options = BlockTreeInsertOptions.BeaconBlockInsert;
                    for (long i = high; i >= low; --i)
                    {
                        BlockHeader? beaconHeader = SyncedTree!.FindHeader(i, BlockTreeLookupOptions.None);
                        
                        if (tdMode == TotalDifficultyMode.Null)
                            beaconHeader!.TotalDifficulty = null;
                        else if (tdMode == TotalDifficultyMode.Zero)
                            beaconHeader.TotalDifficulty = 0;
                        AddBlockResult insertResult = NotSyncedTree!.Insert(beaconHeader!, options);
                        Assert.AreEqual(AddBlockResult.Added, insertResult);
                    }
                    return this;
                }
                
                public ScenarioBuilder InsertBeaconBlocks(long low, long high, TotalDifficultyMode tdMode = TotalDifficultyMode.TheSameAsSyncedTree)
                {
                    BlockTreeInsertOptions insertOptions = BlockTreeInsertOptions.BeaconBlockInsert;
                    for (long i = high; i >= low; --i)
                    {
                        Block? beaconBlock = SyncedTree!.FindBlock(i, BlockTreeLookupOptions.None);
                        if (tdMode == TotalDifficultyMode.Null)
                            beaconBlock!.Header.TotalDifficulty = null;
                        else if (tdMode == TotalDifficultyMode.Zero)
                            beaconBlock!.Header.TotalDifficulty = 0;
                        
                        AddBlockResult insertResult = NotSyncedTree!.Insert(beaconBlock!, true, insertOptions);
                        Assert.AreEqual(AddBlockResult.Added, insertResult);
                    }
                    return this;
                }

                public ScenarioBuilder Restart()
                {
                    NotSyncedTree = new(
                        NotSyncedTreeBuilder!.BlocksDb,
                        NotSyncedTreeBuilder.HeadersDb,
                        NotSyncedTreeBuilder.BlockInfoDb,
                        NotSyncedTreeBuilder.MetadataDb,
                        NotSyncedTreeBuilder.ChainLevelInfoRepository,
                        MainnetSpecProvider.Instance,
                        NullBloomStorage.Instance,
                        new SyncConfig(),
                        LimboLogs.Instance);
                    _chainLevelHelper = new ChainLevelHelper(NotSyncedTree, new SyncConfig(), LimboLogs.Instance);
                    return this;
                }

                public ScenarioBuilder AssertBestKnownNumber(long expected)
                {
                    Assert.AreEqual(expected,NotSyncedTree!.BestKnownNumber);
                    return this;
                }

                public ScenarioBuilder AssertBestSuggestedHeader(long expected)
                {
                    Assert.AreEqual(expected,NotSyncedTree!.BestSuggestedHeader!.Number);
                    return this;
                }

                public ScenarioBuilder AssertBestSuggestedBody(long expected, UInt256? expectedTotalDifficulty = null)
                {
                    Assert.AreEqual(expected,NotSyncedTree!.BestSuggestedBody!.Number);
                    if (expectedTotalDifficulty != null)
                        Assert.AreEqual(expectedTotalDifficulty, NotSyncedTree.BestSuggestedBody.TotalDifficulty);
                    return this;
                }
                
                public ScenarioBuilder AssertMetadata(int startNumber, int finalNumber, BlockMetadata? metadata)
                {
                    for (int i = startNumber; i < finalNumber; ++i)
                    {
                        ChainLevelInfo? level = NotSyncedTree.FindLevel(i);
                        Assert.AreEqual(metadata, level?.BeaconMainChainBlock?.Metadata ?? BlockMetadata.None, $"Block number {i}");
                    }

                    return this;
                }

                public ScenarioBuilder AssertLowestInsertedBeaconHeader(long expected)
                {
                    Assert.IsNotNull(NotSyncedTree);
                    Assert.IsNotNull(NotSyncedTree!.LowestInsertedBeaconHeader);
                    Assert.AreEqual(expected,NotSyncedTree!.LowestInsertedBeaconHeader!.Number);
                    Console.WriteLine("LowestInsertedBeaconHeader:"+NotSyncedTree!.LowestInsertedBeaconHeader!.Number);
                    return this;
                }
                
                public ScenarioBuilder AssertBestBeaconHeader(long expected)
                {
                    Assert.IsNotNull(NotSyncedTree);
                    Assert.IsNotNull(NotSyncedTree.BestSuggestedBeaconHeader);
                    Assert.AreEqual(expected, NotSyncedTree.BestSuggestedBeaconHeader?.Number);
                    return this;
                }

                public ScenarioBuilder AssertBestBeaconBody(long expected)
                {
                    Assert.IsNotNull(NotSyncedTree);
                    Assert.IsNotNull(NotSyncedTree.BestSuggestedBeaconBody);
                    Assert.AreEqual(expected, NotSyncedTree.BestSuggestedBeaconBody?.Number);
                    return this;
                }

                public ScenarioBuilder AssertChainLevel(int startNumber, int finalNumber)
                {
                    for (int i = startNumber; i < finalNumber; ++i)
                    {
                        ChainLevelInfo? level = NotSyncedTree.FindLevel(i);
                        foreach (BlockInfo blockInfo in level.BlockInfos)
                        {
                            Assert.True(blockInfo.TotalDifficulty != null && blockInfo.TotalDifficulty != 0);
                        }
                        
                        Assert.AreEqual(level.BlockInfos.Length, 1);
                    }

                    return this;
                }

                public ScenarioBuilder print()
                {
                    // Console.WriteLine("LowestInsertedBeaconHeader:"+_notSyncedTree!.LowestInsertedBeaconHeader.Number);
                    Console.WriteLine("Head:" + NotSyncedTree!.Head!.Number);
                    Console.WriteLine("BestSuggestedHeader:" + NotSyncedTree!.BestSuggestedHeader.Number);
                    Console.WriteLine("BestSuggestedBody:" + NotSyncedTree!.BestSuggestedBody.Number);
                    // Console.WriteLine("LowestInsertedHeader:"+_notSyncedTree!.LowestInsertedHeader.Number);
                    Console.WriteLine("BestKnownNumber:" + NotSyncedTree!.BestKnownNumber);
                    Console.WriteLine("BestKnownBeaconNumber:" + NotSyncedTree!.BestKnownBeaconNumber);
                    return this;
                }

                public BlockTree SyncedTree { get; private set; }

                public BlockTree NotSyncedTree { get; private set; }

                public BlockTreeBuilder NotSyncedTreeBuilder { get; private set; }
            }

            public static ScenarioBuilder GoesLikeThis()
            {
                return new();
            }
        }
        
    [Test]
    public void Best_pointers_are_set_on_restart_with_gap()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20)
            .InsertBeaconPivot(14)
            .Restart()
            .AssertBestBeaconBody(14)
            .AssertBestBeaconHeader(14)
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }
    
    [Test]
    public void pointers_are_set_on_restart_during_header_sync()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(6, 6)
            .Restart()
            .AssertBestBeaconBody(7)
            .AssertBestBeaconHeader(7)
            .AssertLowestInsertedBeaconHeader(6)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }
    
    [Test]
    public void pointers_are_set_on_restart_after_header_sync_finished()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(4, 6)
            .Restart()
            .AssertBestBeaconBody(7)
            .AssertBestBeaconHeader(7)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }
    
    [Test]
    public void pointers_are_set_on_restart_during_filling_block_gap()
    {
         BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(4, 6)
            .SuggestBlocks(4, 4)
            .Restart()
            .AssertBestBeaconBody(7)
            .AssertBestBeaconHeader(7)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestSuggestedHeader(4)
            .AssertBestSuggestedBody(4);
    }
    
    [Test]
    public void pointers_are_set_on_restart_after_filling_block_gap_finished()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(4, 6)
            .SuggestBlocks(4, 7)
            .ClearBeaconPivot()
            .Restart()
            .AssertBestBeaconBody(0)
            .AssertBestBeaconHeader(0)
            .AssertLowestInsertedBeaconHeader(4)
            .AssertBestSuggestedHeader(7)
            .AssertBestSuggestedBody(7)
            .AssertLowestInsertedBeaconHeader(4);
    }
    
    [Test]
    public void Best_pointers_should_not_move_if_sync_is_not_finished()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(5, 6)
            .InsertBeaconBlocks(7, 9)
            .Restart()
            .AssertBestBeaconBody(9)
            .AssertBestBeaconHeader(9)
            .AssertLowestInsertedBeaconHeader(5)
            .AssertBestKnownNumber(3)
            .AssertBestSuggestedHeader(3)
            .AssertBestSuggestedBody(3);
    }
    

    [Test]
    public void MarkChainAsProcessed_does_not_change_main_chain()
    {
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(9);
        BlockTree blockTree = new(
            blockTreeBuilder.BlocksDb,
            blockTreeBuilder.HeadersDb,
            blockTreeBuilder.BlockInfoDb,
            blockTreeBuilder.MetadataDb,
            blockTreeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        Block? parentBlock = blockTree.FindBlock(8, BlockTreeLookupOptions.None);
        Block newBlock = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(parentBlock!.Header).TestObject).TestObject;
        AddBlockResult addBlockResult =  blockTree.SuggestBlock(newBlock);
        Assert.AreEqual(AddBlockResult.Added,addBlockResult);
        blockTree.MarkChainAsProcessed(new []{ newBlock });
        Assert.False(blockTree.IsMainChain(newBlock.Header));
    }
}

