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

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Synchronization;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task forkChoiceUpdatedV1_unknown_block_initiates_syncing()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak? startingHead = chain.BlockTree.HeadHash;
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(1)
            .WithHash(TestItem.KeccakA)
            .WithNonce(0)
            .WithDifficulty(0)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(2)
            .WithParent(parent)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithAuthor(Address.Zero)
            .WithPostMergeFlag(true)
            .TestObject;
        await rpc.engine_newPayloadV1(new BlockRequestResult(block));
        // sync has not started yet
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(block.Header).Should().BeTrue();
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconPivot.BeaconPivotExists().Should().BeFalse();
        BlockTreePointers pointers = new BlockTreePointers
        {
            BestKnownNumber = 0,
            BestSuggestedHeader = chain.BlockTree.Genesis!,
            BestSuggestedBody = chain.BlockTree.FindBlock(0)!,
            BestKnownBeaconBlock = 0,
            LowestInsertedHeader = null,
            LowestInsertedBeaconHeader = null
        };
        AssertBlockTreePointers(chain.BlockTree, pointers);

        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());

        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.FindBlock(block.Hash)?.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, block.Header);
        pointers.LowestInsertedBeaconHeader = block.Header;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        AssertExecutionStatusNotChangedV1(rpc, block.Hash!, startingHead, startingHead);
    }

    [Test]
    public async Task newPayloadV1_can_insert_blocks_from_cache_when_syncing()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        
        BlockRequestResult parentBlockRequest = new (Build.A.Block.WithNumber(2).TestObject);
        BlockRequestResult[] requests = CreateBlockRequestBranch(parentBlockRequest, Address.Zero, 7);
        ResultWrapper<PayloadStatusV1> payloadStatus;
        foreach (BlockRequestResult r in requests)
        {
            payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Accepted).ToUpper());
            chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
            chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
            chain.BeaconPivot.BeaconPivotExists().Should().BeFalse();
        }
        
        int pivotNum = 3;
        requests[pivotNum].TryGetBlock(out Block? pivotBlock);
        // initiate sync
        ForkchoiceStateV1 forkchoiceStateV1 = new(pivotBlock!.Hash, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // trigger insertion of blocks in cache into block tree
        payloadStatus = await rpc.engine_newPayloadV1(requests[^1]);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // check it is syncing
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(pivotBlock.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, pivotBlock.Header);
        // check correct blocks are inserted
        for (int i = pivotNum; i < requests.Length; i++)
        {
            chain.BlockTree.FindBlock(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
            chain.BlockTree.FindHeader(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
        }
        AssertExecutionStatusNotChangedV1(rpc, pivotBlock.Hash!, startingHead, startingHead);
    }

    [Test]
    public async Task Blocks_from_cache_inserted_when_fast_headers_sync_finish_before_newPayloadV1_request()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        Keccak startingHead = chain.BlockTree.HeadHash;
        IEngineRpcModule rpc = CreateEngineModule(chain);
        BlockRequestResult[] requests = CreateBlockRequestBranch(new(chain.BlockTree.Head!), Address.Zero, 7);

        ResultWrapper<PayloadStatusV1> payloadStatus;
        for (int i = 4; i < requests.Length - 1; i++)
        {
            payloadStatus = await rpc.engine_newPayloadV1(requests[i]);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Accepted).ToUpper());
        }
        // use third last block request as beacon pivot so there are blocks in cache
        ForkchoiceStateV1 forkchoiceStateV1 = new(requests[^3].BlockHash, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // complete headers sync
        BlockTreeInsertOptions options = BlockTreeInsertOptions.UpdateBeaconPointers
                                         | BlockTreeInsertOptions.SkipUpdateBestPointers
                                         | BlockTreeInsertOptions.TotalDifficultyNotNeeded;
        for (int i = 0; i < requests.Length - 2; i++)
        {
            requests[i].TryGetBlock(out Block? block);
            chain.BlockTree.Insert(block!.Header, options);
        }
        // trigger dangling block insert
        payloadStatus = await rpc.engine_newPayloadV1(requests[^1]);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // b0 <- h1 ... <- b5 <- b6 <- b7
        // check state is correct and beacon blocks in cache inserted
        for (int i = requests.Length; i --> requests.Length - 3;)
        {
            chain.BlockTree.FindBlock(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
            chain.BlockTree.FindHeader(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
        }
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeFalse();
    }
    
    [Test]
    [Ignore("ToDo should be fixed with restarts and pointers")]
    public async Task Maintain_correct_pointers_for_beacon_sync_in_archive_sync()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        // create 7 block gap
        int gap = 7;
        BlockRequestResult headBlockRequest = new(chain.BlockTree.Head!);
        Block[] missingBlocks = new Block[gap];
        for (int i = 0; i < gap; i++)
        {
            headBlockRequest = CreateBlockRequest(headBlockRequest, Address.Zero); 
            headBlockRequest.TryGetBlock(out Block? block);
            missingBlocks[i] = block!;
        }
        // setting up beacon pivot
        BlockRequestResult pivotRequest = CreateBlockRequest(headBlockRequest, Address.Zero);
        ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(pivotRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Accepted).ToUpper());
        pivotRequest.TryGetBlock(out Block? pivotBlock);
        // check block tree pointers
        BlockTreePointers pointers = new BlockTreePointers
        {
            BestKnownNumber = 0,
            BestSuggestedHeader = chain.BlockTree.Genesis!,
            BestSuggestedBody = chain.BlockTree.FindBlock(0)!,
            BestKnownBeaconBlock = 0,
            LowestInsertedHeader = null,
            LowestInsertedBeaconHeader = null
        };
        AssertBlockTreePointers(chain.BlockTree, pointers);
        // initiate sync
        ForkchoiceStateV1 forkchoiceStateV1 = new(pivotBlock!.Hash, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // trigger insertion of blocks in cache into block tree by adding new block
        BlockRequestResult bestBeaconBlockRequest = CreateBlockRequest(pivotRequest, Address.Zero);
        payloadStatus = await rpc.engine_newPayloadV1(bestBeaconBlockRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // simulate headers sync by inserting 3 headers from pivot backwards
        int filledNum = 3;
        BlockTreeInsertOptions options = BlockTreeInsertOptions.SkipUpdateBestPointers |
                                         BlockTreeInsertOptions.TotalDifficultyNotNeeded |
                                         BlockTreeInsertOptions.UpdateBeaconPointers;
        for (int i = missingBlocks.Length; i --> missingBlocks.Length - filledNum;)
        {
            chain.BlockTree.Insert(missingBlocks[i].Header, options);
        }
        // b0 <- ... h5 <- h6 <- h7 <- b8 <- b9
        // b8: beacon pivot, h5: lowest inserted headers
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(pivotBlock.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, pivotBlock.Header);
        pointers.LowestInsertedBeaconHeader = missingBlocks[^filledNum].Header;
        pointers.LowestInsertedHeader = missingBlocks[^filledNum].Header;
        pointers.BestKnownBeaconBlock = 9;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        // finish rest of headers sync
        for (int i = missingBlocks.Length - filledNum; i --> 0;)
        {
            chain.BlockTree.Insert(missingBlocks[i].Header, options);
        }
        // headers sync should be finished but not forwards beacon sync
        pointers.LowestInsertedBeaconHeader = missingBlocks[0].Header;
        pointers.LowestInsertedHeader = missingBlocks[0].Header;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeFalse();
        
        // finish beacon forwards sync
        foreach (Block block in missingBlocks)
        {
            await chain.BlockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ShouldProcess | BlockTreeSuggestOptions.FillBeaconBlock);
        }
        bestBeaconBlockRequest.TryGetBlock(out Block? bestBeaconBlock);
        SemaphoreSlim bestBlockProcessed = new(0);
        chain.BlockProcessor.BlockProcessed += (s, e) =>
        {
            if (e.Block.Hash == bestBeaconBlock!.Hash)
                    bestBlockProcessed.Release(1);
        };
        await chain.BlockTree.SuggestBlockAsync(bestBeaconBlock!, BlockTreeSuggestOptions.ShouldProcess | BlockTreeSuggestOptions.FillBeaconBlock);

        await bestBlockProcessed.WaitAsync();
        
        // beacon sync should be finished
        bestBeaconBlockRequest = CreateBlockRequest(bestBeaconBlockRequest, Address.Zero);
        payloadStatus = await rpc.engine_newPayloadV1(bestBeaconBlockRequest);
        payloadStatus.Data.Status.Should().Be(PayloadStatus.Valid);
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeTrue();
    }

    [Test]
    public async Task Maintain_correct_pointers_for_beacon_sync_in_fast_sync()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        BlockTree syncedBlockTree = Build.A.BlockTree(chain.BlockTree.Head!).OfChainLength(5).TestObject;
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = syncedBlockTree.Head?.Number.ToString() ?? "",
            PivotHash = syncedBlockTree.HeadHash?.ToString() ?? "",
            PivotTotalDifficulty = syncedBlockTree.Head?.TotalDifficulty?.ToString() ?? ""
        };
        IEngineRpcModule rpc = CreateEngineModule(chain, syncConfig);
        // create block gap from fast sync pivot
        int gap = 7;
        BlockRequestResult[] requests =
            CreateBlockRequestBranch(new BlockRequestResult(syncedBlockTree.Head!), Address.Zero, gap);
        // setting up beacon pivot
        BlockRequestResult pivotRequest = CreateBlockRequest(requests[^1], Address.Zero);
        ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(pivotRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Accepted).ToUpper());
        pivotRequest.TryGetBlock(out Block? pivotBlock);
        // check block tree pointers
        BlockTreePointers pointers = new BlockTreePointers
        {
            BestKnownNumber = 0,
            BestSuggestedHeader = chain.BlockTree.Genesis!,
            BestSuggestedBody = chain.BlockTree.FindBlock(0)!,
            BestKnownBeaconBlock = 0,
            LowestInsertedHeader = null,
            LowestInsertedBeaconHeader = null
        };
        AssertBlockTreePointers(chain.BlockTree, pointers);
        // initiate sync
        Keccak startingHead = chain.BlockTree.HeadHash;
        ForkchoiceStateV1 forkchoiceStateV1 = new(pivotBlock!.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // trigger insertion of blocks in cache into block tree by adding new block
        BlockRequestResult bestBeaconBlockRequest = CreateBlockRequest(pivotRequest, Address.Zero);
        payloadStatus = await rpc.engine_newPayloadV1(bestBeaconBlockRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // fill in beacon headers until fast headers pivot
        BlockTreeInsertOptions options = BlockTreeInsertOptions.SkipUpdateBestPointers |
                                         BlockTreeInsertOptions.TotalDifficultyNotNeeded |
                                         BlockTreeInsertOptions.UpdateBeaconPointers;
        for (int i = requests.Length; i --> 0;)
        {
            requests[i].TryGetBlock(out Block? block);
            chain.BlockTree.Insert(block!.Header, options);
        }
        // verify correct pointers
        requests[0].TryGetBlock(out Block? destinationBlock);
        pointers.LowestInsertedBeaconHeader = destinationBlock!.Header;
        pointers.LowestInsertedHeader = destinationBlock.Header;
        pointers.BestKnownBeaconBlock = 13;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeFalse();
        // TODO: post merge sync checking pointers after state sync
    }

    [Test]
    [Ignore("TODO: post merge sync able to handle multiple pivots")]
    public async Task Maintain_correct_pointers_for_multiple_pivots() { }

    private void AssertBlockTreePointers(
        IBlockTree blockTree,
        BlockTreePointers pointers)
    {
        blockTree.BestKnownNumber.Should().Be(pointers.BestKnownNumber);
        blockTree.BestSuggestedHeader.Should().Be(pointers.BestSuggestedHeader);
        blockTree.BestSuggestedBody.Should().Be(pointers.BestSuggestedBody);
        // TODO: post merge sync change to best beacon block
        (blockTree.BestSuggestedBeaconHeader?.Number ?? 0).Should().Be(pointers.BestKnownBeaconBlock);
        blockTree.LowestInsertedHeader.Should().BeEquivalentTo(pointers.LowestInsertedHeader);
        blockTree.LowestInsertedBeaconHeader.Should().BeEquivalentTo(pointers.LowestInsertedBeaconHeader);
    }

    private void AssertBeaconPivotValues(IBeaconPivot beaconPivot, BlockHeader blockHeader)
    {
        beaconPivot.BeaconPivotExists().Should().BeTrue();
        beaconPivot.PivotNumber.Should().Be(blockHeader.Number);
        beaconPivot.PivotHash.Should().Be(blockHeader.Hash ?? blockHeader.CalculateHash());
        beaconPivot.PivotTotalDifficulty.Should().Be(null);
    }

    private class BlockTreePointers
    {
        public long BestKnownNumber;
        public BlockHeader BestSuggestedHeader;
        public Block BestSuggestedBody;
        public long BestKnownBeaconBlock;
        public BlockHeader? LowestInsertedHeader;
        public BlockHeader? LowestInsertedBeaconHeader;
    }
}
