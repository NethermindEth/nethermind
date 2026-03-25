// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Handlers;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin;
using Nethermind.Taiko.Rpc;

namespace Nethermind.Taiko.Test;

public class TaikoEngineApiTests
{
    [Test]
    public async Task Test_ForkchoiceUpdatedHandler_Allows_UnknownFinalizedSafeBlocks()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
        Block futureBlock = Build.A.Block.WithNumber(1).TestObject;

        AddBlock(blockTree, genesisBlock);

        TaikoForkchoiceUpdatedHandler forkchoiceUpdatedHandler = new(
            blockTree,
            Substitute.For<IManualBlockFinalizationManager>(),
            Substitute.For<IPoSSwitcher>(),
            Substitute.For<IPayloadPreparationService>(),
            Substitute.For<IBlockProcessingQueue>(),
            Substitute.For<IBlockCacheService>(),
            Substitute.For<IInvalidChainTracker>(),
            Substitute.For<IMergeSyncController>(),
            Substitute.For<IBeaconPivot>(),
            Substitute.For<IPeerRefresher>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<ISyncPeerPool>(),
            new MergeConfig(),
            Substitute.For<ILogManager>()
        );

        ResultWrapper<ForkchoiceUpdatedV1Result> beforeNewBlockAdded = await forkchoiceUpdatedHandler.Handle(new ForkchoiceStateV1(genesisBlock.Hash!, futureBlock.Hash!, futureBlock.Hash!), null, 2);
        Assert.That(beforeNewBlockAdded.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        AddBlock(blockTree, futureBlock);

        ResultWrapper<ForkchoiceUpdatedV1Result> afterNewBlockAdded = await forkchoiceUpdatedHandler.Handle(new ForkchoiceStateV1(futureBlock.Hash!, futureBlock.Hash!, futureBlock.Hash!), null, 2);
        Assert.That(afterNewBlockAdded.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));

        static void AddBlock(IBlockTree blockTree, Block block)
        {
            blockTree.FindBlock(block.Hash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(block);
            blockTree.GetInfo(block.Number, block.Hash!).Returns((new BlockInfo(block.Hash!, 0) { WasProcessed = true }, new ChainLevelInfo(true)));
            blockTree.Head.Returns(block);
            blockTree.HeadHash.Returns(block.Hash!);
        }
    }

    [TestCase(100ul, 100ul, true, TestName = "Equal timestamps allowed for Pacaya")]
    [TestCase(100ul, 101ul, true, TestName = "Greater timestamp allowed")]
    [TestCase(100ul, 99ul, false, TestName = "Lesser timestamp rejected")]
    public async Task Test_ForkchoiceUpdatedHandler_Allows_Equal_Timestamps(ulong headTimestamp, ulong payloadTimestamp, bool shouldSucceed)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        Block headBlock = Build.A.Block.WithNumber(1).WithTimestamp(headTimestamp).TestObject;

        blockTree.FindBlock(headBlock.Hash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing).Returns(headBlock);
        blockTree.GetInfo(headBlock.Number, headBlock.Hash!).Returns((new BlockInfo(headBlock.Hash!, 0) { WasProcessed = true }, new ChainLevelInfo(true)));
        blockTree.Head.Returns(headBlock);
        blockTree.HeadHash.Returns(headBlock.Hash!);
        blockTree.IsMainChain(headBlock.Header).Returns(true);

        TaikoForkchoiceUpdatedHandler handler = new(
            blockTree,
            Substitute.For<IManualBlockFinalizationManager>(),
            Substitute.For<IPoSSwitcher>(),
            Substitute.For<IPayloadPreparationService>(),
            Substitute.For<IBlockProcessingQueue>(),
            Substitute.For<IBlockCacheService>(),
            Substitute.For<IInvalidChainTracker>(),
            Substitute.For<IMergeSyncController>(),
            Substitute.For<IBeaconPivot>(),
            Substitute.For<IPeerRefresher>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<ISyncPeerPool>(),
            new MergeConfig(),
            Substitute.For<ILogManager>()
        );

        PayloadAttributes payloadAttributes = new()
        {
            Timestamp = payloadTimestamp,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero
        };

        ForkchoiceStateV1 forkchoiceState = new(headBlock.Hash!, headBlock.Hash!, headBlock.Hash!);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await handler.Handle(forkchoiceState, payloadAttributes, 1);

        if (shouldSucceed)
        {
            Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        }
        else
        {
            Assert.That(result.Result.Error, Does.Contain("Invalid payload timestamp"));
        }
    }
}
