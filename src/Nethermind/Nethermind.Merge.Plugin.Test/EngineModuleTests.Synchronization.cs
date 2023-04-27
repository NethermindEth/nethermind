// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
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
        await rpc.engine_newPayloadV1(new ExecutionPayload(block));
        // sync has not started yet
        chain.BeaconSync!.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(block.Header).Should().BeTrue();
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconPivot!.BeaconPivotExists().Should().BeFalse();
        BlockTreePointers pointers = new()
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
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.FindBlock(block.Hash!)?.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, block.Header);
        pointers.LowestInsertedBeaconHeader = block.Header;
        pointers.BestKnownBeaconBlock = block.Number;
        pointers.LowestInsertedHeader = block.Header;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        AssertExecutionStatusNotChangedV1(chain.BlockFinder, block.Hash!, startingHead, startingHead);
    }

    [Test]
    public async Task forkChoiceUpdatedV1_unknown_block_parent_while_syncing_initiates_new_sync()
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

        Block nextUnconnectedBlock = Build.A.Block
            .WithNumber(3)
            .WithParent(block)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithAuthor(Address.Zero)
            .WithPostMergeFlag(true)
            .TestObject;

        nextUnconnectedBlock = Build.A.Block
            .WithNumber(4)
            .WithParent(nextUnconnectedBlock)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithAuthor(Address.Zero)
            .WithPostMergeFlag(true)
            .TestObject;

        await rpc.engine_newPayloadV1(new ExecutionPayload(block));
        // sync has not started yet
        chain.BeaconSync!.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(block.Header).Should().BeTrue();
        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconPivot!.BeaconPivotExists().Should().BeFalse();
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
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.FindBlock(block.Hash!)?.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, block.Header);

        pointers.LowestInsertedBeaconHeader = block.Header;
        pointers.BestKnownBeaconBlock = block.Number;
        pointers.LowestInsertedHeader = block.Header;
        AssertBlockTreePointers(chain.BlockTree, pointers);

        await rpc.engine_newPayloadV1(new ExecutionPayload(nextUnconnectedBlock));
        forkchoiceStateV1 = new(nextUnconnectedBlock.Hash!, startingHead, startingHead);
        forkchoiceUpdatedResult = await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());

        chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.FindBlock(nextUnconnectedBlock.Hash!)?.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot, nextUnconnectedBlock.Header);

        pointers.LowestInsertedBeaconHeader = nextUnconnectedBlock.Header;
        pointers.BestKnownBeaconBlock = nextUnconnectedBlock.Number;
        AssertBlockTreePointers(chain.BlockTree, pointers);

        AssertExecutionStatusNotChangedV1(chain.BlockFinder, block.Hash!, startingHead, startingHead);
    }

    [Test]
    public async Task should_return_invalid_lvh_null_on_invalid_blocks_during_the_sync()
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
        ExecutionPayload startingNewPayload = new(block);
        await rpc.engine_newPayloadV1(startingNewPayload);

        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());

        ExecutionPayload[] requests = CreateBlockRequestBranch(startingNewPayload, TestItem.AddressD, 1);
        foreach (ExecutionPayload r in requests)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        }

        ExecutionPayload[] invalidRequests = CreateBlockRequestBranch(requests[0], TestItem.AddressD, 1);
        foreach (ExecutionPayload r in invalidRequests)
        {
            r.TryGetBlock(out Block? newBlock);
            newBlock!.Header.GasLimit = long.MaxValue; // incorrect gas limit
            newBlock.Header.Hash = newBlock.CalculateHash();
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(new ExecutionPayload(newBlock));
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Invalid).ToUpper());
            payloadStatus.Data.LatestValidHash.Should().BeNull();
        }
    }

    [Test]
    public async Task newPayloadV1_can_insert_blocks_from_cache_when_syncing()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;

        ExecutionPayload parentBlockRequest = new(Build.A.Block.WithNumber(2).TestObject);
        ExecutionPayload[] requests = CreateBlockRequestBranch(parentBlockRequest, Address.Zero, 7);
        ResultWrapper<PayloadStatusV1> payloadStatus;
        foreach (ExecutionPayload r in requests)
        {
            payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            chain.BeaconSync!.IsBeaconSyncHeadersFinished().Should().BeTrue();
            chain.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
            chain.BeaconPivot!.BeaconPivotExists().Should().BeFalse();
        }

        int pivotNum = 3;
        requests[pivotNum].TryGetBlock(out Block? pivotBlock);
        // initiate sync
        ForkchoiceStateV1 forkchoiceStateV1 = new(pivotBlock!.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // trigger insertion of blocks in cache into block tree
        payloadStatus = await rpc.engine_newPayloadV1(requests[^1]);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // check it is syncing
        chain.BeaconSync!.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(pivotBlock.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot!, pivotBlock.Header);
        // check correct blocks are inserted
        for (int i = pivotNum; i < requests.Length; i++)
        {
            chain.BlockTree.FindBlock(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
            chain.BlockTree.FindHeader(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
        }

        AssertExecutionStatusNotChangedV1(chain.BlockFinder, pivotBlock.Hash!, startingHead, startingHead);
    }

    [Test]
    public async Task first_new_payload_set_beacon_main_chain()
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
        ExecutionPayload startingNewPayload = new(block);
        await rpc.engine_newPayloadV1(startingNewPayload);
        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        ExecutionPayload[] requests = CreateBlockRequestBranch(startingNewPayload, Address.Zero, 4);
        foreach (ExecutionPayload r in requests)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            lvl.Should().NotBeNull();
            lvl!.BlockInfos.Length.Should().Be(1);
            lvl!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
        }

        AssertBeaconPivotValues(chain.BeaconPivot!, block.Header);
    }

    [Test]
    public async Task repeated_new_payloads_do_not_change_metadata()
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
        ExecutionPayload startingNewPayload = new(block);
        await rpc.engine_newPayloadV1(startingNewPayload);
        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        ExecutionPayload[] requests = CreateBlockRequestBranch(startingNewPayload, Address.Zero, 4);
        foreach (ExecutionPayload r in requests)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            lvl.Should().NotBeNull();
            lvl!.BlockInfos.Length.Should().Be(1);
            lvl!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
        }

        foreach (ExecutionPayload r in requests)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            lvl.Should().NotBeNull();
            lvl!.BlockInfos.Length.Should().Be(1);
            lvl!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
        }
    }

    [Test]
    public async Task Can_set_beacon_pivot_in_new_payload_if_null()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak lastHash = (await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true)).LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();

        Block newBlock = Build.A.Block.WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithParent(chain.BlockTree.Head!)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock.CalculateHash();
        await chain.BlockTree.SuggestBlockAsync(newBlock, BlockTreeSuggestOptions.None);

        Block newBlock2 = Build.A.Block.WithNumber(chain.BlockTree.BestSuggestedBody!.Number + 1)
            .WithParent(chain.BlockTree.BestSuggestedBody!)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock2.CalculateHash();

        chain.BeaconPivot!.BeaconPivotExists().Should().BeFalse();
        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV1(new ExecutionPayload(newBlock2));
        result.Data.Status.Should().Be(PayloadStatus.Syncing);
        chain.BeaconPivot.BeaconPivotExists().Should().BeTrue();
    }


    [Test]
    public async Task BeaconMainChain_is_correctly_set_when_block_was_not_processed()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak lastHash = (await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true)).LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();

        Block newBlock = Build.A.Block.WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithParent(chain.BlockTree.Head!)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock.CalculateHash();
        await chain.BlockTree.SuggestBlockAsync(newBlock, BlockTreeSuggestOptions.None);

        Block newBlock2 = Build.A.Block.WithNumber(chain.BlockTree.BestSuggestedBody!.Number + 1)
            .WithParent(chain.BlockTree.BestSuggestedBody!)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock2.CalculateHash();

        await rpc.engine_newPayloadV1(new ExecutionPayload(newBlock2));

        await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(newBlock2.Hash!, newBlock2.Hash!, newBlock2.Hash!), null);
        chain.BlockTree.FindLevel(10)!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.None);
        chain.BlockTree.FindLevel(newBlock2.Number)!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconMainChain | BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody);
    }


    [Test]
    public async Task Repeated_block_do_not_change_metadata()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak lastHash = (await ProduceBranchV1(rpc, chain, 20, CreateParentBlockRequestOnHead(chain.BlockTree), true)).LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);
        Block? last = RunForAllBlocksInBranch(chain.BlockTree, chain.BlockTree.HeadHash, b => b.IsGenesis, true);
        last.Should().NotBeNull();
        last!.IsGenesis.Should().BeTrue();

        Block newBlock = Build.A.Block.WithNumber(chain.BlockTree.Head!.Number + 1)
            .WithParent(chain.BlockTree.Head!)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock.CalculateHash();
        await chain.BlockTree.SuggestBlockAsync(newBlock!, BlockTreeSuggestOptions.FillBeaconBlock);


        Block newBlock2 = Build.A.Block.WithNumber(newBlock.Number + 1)
            .WithParent(newBlock)
            .WithNonce(0)
            .WithDifficulty(0)
            .WithPostMergeFlag(true)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f")).TestObject;
        newBlock2.CalculateHash();
        await chain.BlockTree.SuggestBlockAsync(newBlock2!, BlockTreeSuggestOptions.FillBeaconBlock);

        await rpc.engine_newPayloadV1(new ExecutionPayload(newBlock2));
        Block? block = chain.BlockTree.FindBlock(newBlock2.GetOrCalculateHash(), BlockTreeLookupOptions.None);
        block?.TotalDifficulty.Should().NotBe((UInt256)0);
        BlockInfo? blockInfo = chain.BlockTree.FindLevel(newBlock2.Number!)?.BlockInfos[0];
        blockInfo?.TotalDifficulty.Should().NotBe(0);
        blockInfo?.Metadata.Should().Be(BlockMetadata.None);
    }

    [Test]
    public async Task second_new_payload_should_not_set_beacon_main_chain()
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
        ExecutionPayload startingNewPayload = new(block);
        await rpc.engine_newPayloadV1(startingNewPayload);
        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        ExecutionPayload[] requests = CreateBlockRequestBranch(startingNewPayload, Address.Zero, 4);
        foreach (ExecutionPayload r in requests)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            lvl.Should().NotBeNull();
            lvl!.BlockInfos.Length.Should().Be(1);
            lvl!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
        }

        ExecutionPayload[] secondNewPayloads = CreateBlockRequestBranch(startingNewPayload, TestItem.AddressD, 4);
        foreach (ExecutionPayload r in secondNewPayloads)
        {
            ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(r);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            lvl.Should().NotBeNull();
            lvl!.BlockInfos.Length.Should().Be(2);
            lvl!.BlockInfos[0].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
            lvl!.BlockInfos[1].Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        }
    }

    [TestCase(4, 4)]
    [TestCase(4, 6)] // reorged to higher chain
    [TestCase(5, 3)] // reorged to lower chain
    [TestCase(7, 6, 4)]
    [TestCase(2, 3)]
    [TestCase(3, 2)]
    [TestCase(4, 4, 1)]
    [TestCase(4, 3, 0)]
    [TestCase(3, 3, 0)]
    [TestCase(3, 4, 0)]
    public async Task should_reorg_during_the_sync(int initialChainPayloadsCount, int reorgedChainPayloadCount, int? reorgToIndex = null)
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
        ExecutionPayload startingNewPayload = new(block);
        await rpc.engine_newPayloadV1(startingNewPayload);
        ForkchoiceStateV1 forkchoiceStateV1 = new(block.Hash!, startingHead, startingHead);
        await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        ExecutionPayload[] initialBranchPayloads = CreateBlockRequestBranch(startingNewPayload, Address.Zero, initialChainPayloadsCount);
        foreach (ExecutionPayload r in initialBranchPayloads)
        {
            await rpc.engine_newPayloadV1(r);
        }

        ExecutionPayload[] newBranchPayloads = CreateBlockRequestBranch(startingNewPayload, TestItem.AddressD, reorgedChainPayloadCount);
        foreach (ExecutionPayload r in newBranchPayloads)
        {
            await rpc.engine_newPayloadV1(r);
        }

        Keccak lastHash = newBranchPayloads[reorgToIndex ?? ^1].BlockHash!;
        ForkchoiceStateV1 forkchoiceStateV1Reorg = new(lastHash, lastHash, lastHash);
        await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1Reorg);

        IEnumerable<ExecutionPayload> mainChainRequests = newBranchPayloads.Take(reorgToIndex + 1 ?? newBranchPayloads.Length);
        ExecutionPayload[] requestsToIterate = newBranchPayloads.Length >= initialBranchPayloads.Length ? newBranchPayloads : initialBranchPayloads;
        foreach (ExecutionPayload r in requestsToIterate)
        {
            ChainLevelInfo? lvl = chain.BlockTree.FindLevel(r.BlockNumber);
            foreach (BlockInfo blockInfo in lvl!.BlockInfos)
            {
                if (mainChainRequests!.Any(x => x.BlockHash == blockInfo.BlockHash))
                    blockInfo.Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain, $"BlockNumber {r.BlockNumber}");
                else
                    blockInfo.Metadata.Should().Be(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader, $"BlockNumber {r.BlockNumber}");
            }
        }
    }

    [Test]
    public async Task Blocks_from_cache_inserted_when_fast_headers_sync_finish_before_newPayloadV1_request()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        Keccak startingHead = chain.BlockTree.HeadHash;
        IEngineRpcModule rpc = CreateEngineModule(chain);
        ExecutionPayload[] requests = CreateBlockRequestBranch(new ExecutionPayload(chain.BlockTree.Head!), Address.Zero, 7);

        ResultWrapper<PayloadStatusV1> payloadStatus;
        for (int i = 4; i < requests.Length - 1; i++)
        {
            payloadStatus = await rpc.engine_newPayloadV1(requests[i]);
            payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        }

        // use third last block request as beacon pivot so there are blocks in cache
        ForkchoiceStateV1 forkchoiceStateV1 = new(requests[^3].BlockHash, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // complete headers sync
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderMetadata
                                                     | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
        for (int i = 0; i < requests.Length - 2; i++)
        {
            requests[i].TryGetBlock(out Block? block);
            chain.BlockTree.Insert(block!.Header, headerOptions);
        }

        // trigger dangling block insert
        payloadStatus = await rpc.engine_newPayloadV1(requests[^1]);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // b0 <- h1 ... <- b5 <- b6 <- b7
        // check state is correct and beacon blocks in cache inserted
        for (int i = requests.Length; i-- > requests.Length - 3;)
        {
            chain.BlockTree.FindBlock(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
            chain.BlockTree.FindHeader(requests[i].BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
        }

        chain.BeaconSync!.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeFalse();
    }

    [Test]
    public async Task Maintain_correct_pointers_for_beacon_sync_in_archive_sync()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak startingHead = chain.BlockTree.HeadHash;
        // create 7 block gap
        int gap = 7;
        ExecutionPayload headBlockRequest = new(chain.BlockTree.Head!);
        Block[] missingBlocks = new Block[gap];
        for (int i = 0; i < gap; i++)
        {
            headBlockRequest = CreateBlockRequest(headBlockRequest, Address.Zero);
            headBlockRequest.TryGetBlock(out Block? block);
            missingBlocks[i] = block!;
        }

        // setting up beacon pivot
        ExecutionPayload pivotRequest = CreateBlockRequest(headBlockRequest, Address.Zero);
        ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(pivotRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        pivotRequest.TryGetBlock(out Block? pivotBlock);
        // check block tree pointers
        BlockTreePointers pointers = new()
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
        ForkchoiceStateV1 forkchoiceStateV1 = new(pivotBlock!.Hash!, startingHead, startingHead);
        ResultWrapper<ForkchoiceUpdatedV1Result> forkchoiceUpdatedResult =
            await rpc.engine_forkchoiceUpdatedV1(forkchoiceStateV1);
        forkchoiceUpdatedResult.Data.PayloadStatus.Status.Should()
            .Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // trigger insertion of blocks in cache into block tree by adding new block
        ExecutionPayload bestBeaconBlockRequest = CreateBlockRequest(pivotRequest, Address.Zero);
        payloadStatus = await rpc.engine_newPayloadV1(bestBeaconBlockRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // simulate headers sync by inserting 3 headers from pivot backwards
        int filledNum = 3;
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderMetadata |
                                                     BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
        for (int i = missingBlocks.Length; i-- > missingBlocks.Length - filledNum;)
        {
            chain.BlockTree.Insert(missingBlocks[i].Header, headerOptions);
        }

        // b0 <- ... h5 <- h6 <- h7 <- b8 <- b9
        // b8: beacon pivot, h5: lowest inserted headers
        chain.BeaconSync!.ShouldBeInBeaconHeaders().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncFinished(pivotBlock.Header).Should().BeFalse();
        AssertBeaconPivotValues(chain.BeaconPivot!, pivotBlock.Header);
        pointers.LowestInsertedBeaconHeader = missingBlocks[^filledNum].Header;
        pointers.LowestInsertedHeader = missingBlocks[^filledNum].Header;
        pointers.BestKnownBeaconBlock = 9;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        // finish rest of headers sync
        for (int i = missingBlocks.Length - filledNum; i-- > 0;)
        {
            chain.BlockTree.Insert(missingBlocks[i].Header, headerOptions);
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

        // beacon sync should be finished, eventually
        bestBeaconBlockRequest = CreateBlockRequest(bestBeaconBlockRequest, Address.Zero);
        Assert.That(
            () => rpc.engine_newPayloadV1(bestBeaconBlockRequest).Result.Data.Status,
            Is.EqualTo(PayloadStatus.Valid).After(1000, 100)
        );

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
        ExecutionPayload[] requests =
            CreateBlockRequestBranch(new ExecutionPayload(syncedBlockTree.Head!), Address.Zero, gap);
        // setting up beacon pivot
        ExecutionPayload pivotRequest = CreateBlockRequest(requests[^1], Address.Zero);
        ResultWrapper<PayloadStatusV1> payloadStatus = await rpc.engine_newPayloadV1(pivotRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        pivotRequest.TryGetBlock(out Block? pivotBlock);
        // check block tree pointers
        BlockTreePointers pointers = new()
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
        ExecutionPayload bestBeaconBlockRequest = CreateBlockRequest(pivotRequest, Address.Zero);
        payloadStatus = await rpc.engine_newPayloadV1(bestBeaconBlockRequest);
        payloadStatus.Data.Status.Should().Be(nameof(PayloadStatusV1.Syncing).ToUpper());
        // fill in beacon headers until fast headers pivot
        BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderMetadata |
                                                     BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
        for (int i = requests.Length; i-- > 0;)
        {
            requests[i].TryGetBlock(out Block? block);
            chain.BlockTree.Insert(block!.Header, headerOptions);
        }

        // verify correct pointers
        requests[0].TryGetBlock(out Block? destinationBlock);
        pointers.LowestInsertedBeaconHeader = destinationBlock!.Header;
        pointers.LowestInsertedHeader = destinationBlock.Header;
        pointers.BestKnownBeaconBlock = 13;
        AssertBlockTreePointers(chain.BlockTree, pointers);
        chain.BeaconSync!.ShouldBeInBeaconHeaders().Should().BeFalse();
        chain.BeaconSync.IsBeaconSyncHeadersFinished().Should().BeTrue();
        chain.BeaconSync.IsBeaconSyncFinished(chain.BlockTree.BestSuggestedBeaconHeader).Should().BeFalse();
        // TODO: post merge sync checking pointers after state sync
    }

    [Test]
    public async Task Invalid_block_can_create_invalid_best_state_issue_but_recalculating_tree_levels_will_fix_it()
    {
        using MergeTestBlockchain chain = await CreateBlockChain();
        IEngineRpcModule rpc = CreateEngineModule(chain);
        Keccak lastHash = (await ProduceBranchV1(rpc, chain, 30, CreateParentBlockRequestOnHead(chain.BlockTree), true))
            .LastOrDefault()?.BlockHash ?? Keccak.Zero;
        chain.BlockTree.HeadHash.Should().Be(lastHash);

        // send newPayload
        ExecutionPayload validBlockOnTopOfHead = CreateBlockRequest(CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        PayloadStatusV1 payloadStatusResponse = (await rpc.engine_newPayloadV1(validBlockOnTopOfHead)).Data;
        payloadStatusResponse.Status.Should().Be(PayloadStatus.Valid);

        // send block with invalid state root
        ExecutionPayload blockWithInvalidStateRoot = CreateBlockRequest(validBlockOnTopOfHead, TestItem.AddressA);
        blockWithInvalidStateRoot.StateRoot = TestItem.KeccakB;
        TryCalculateHash(blockWithInvalidStateRoot, out Keccak? hash);
        blockWithInvalidStateRoot.BlockHash = hash;
        PayloadStatusV1 invalidStateRootNewPayloadResponse = (await rpc.engine_newPayloadV1(blockWithInvalidStateRoot)).Data;
        invalidStateRootNewPayloadResponse.Status.Should().Be(PayloadStatus.Invalid);

        // send fcU to last new payload
        ForkchoiceUpdatedV1Result response = (await rpc.engine_forkchoiceUpdatedV1(new ForkchoiceStateV1(validBlockOnTopOfHead.BlockHash, validBlockOnTopOfHead.BlockHash, validBlockOnTopOfHead.BlockHash))).Data;
        response.PayloadStatus.Status.Should().Be(PayloadStatus.Valid);

        // invalid best state calculation
        Assert.True(chain.BlockTree.BestSuggestedBody!.Number < chain.BlockTree.Head!.Number);
        Assert.True(chain.BlockTree.BestSuggestedHeader!.Number < chain.BlockTree.Head!.Number);

        // autofix
        chain.BlockTree.RecalculateTreeLevels();

        Assert.True(chain.BlockTree.BestSuggestedBody!.Number >= chain.BlockTree.Head!.Number);
        Assert.True(chain.BlockTree.BestSuggestedHeader!.Number < chain.BlockTree.Head!.Number);
    }

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
        public BlockHeader? BestSuggestedHeader;
        public Block? BestSuggestedBody;
        public long BestKnownBeaconBlock;
        public BlockHeader? LowestInsertedHeader;
        public BlockHeader? LowestInsertedBeaconHeader;
    }
}
