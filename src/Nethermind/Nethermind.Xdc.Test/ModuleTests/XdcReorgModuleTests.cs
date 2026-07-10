// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class XdcReorgModuleTests
{
    [Test]
    public async Task TestNormalReorgWhenNotInvolveCommittedBlock()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create();
        ulong startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(3);
        // Simulate timeout to make block rounds non-consecutive preventing finalization
        blockChain.XdcContext.SetNewRound(blockChain.XdcContext.CurrentRound + 1);
        await blockChain.AddBlocks(2);
        BlockRoundInfo finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        Assert.That(finalizedBlockInfo.Round, Is.EqualTo(blockChain.XdcContext.CurrentRound - 3 - 1 - 2)); // Finalization is 3 round behind current plus 1 round timeout plus 2 rounds of blocks added

        BlockHeader? finalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash);

        Block? forkBlock = await blockChain.BlockProducer.BuildBlock(finalizedBlock);

        Assert.That(blockChain.BlockTree.SuggestBlock(forkBlock!), Is.EqualTo(Blockchain.AddBlockResult.Added));
    }

    [Test]
    public async Task BuildAValidForkOnFinalizedBlockAndAssertForkBecomesCanonical()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(3);
        ulong startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(10);

        BlockRoundInfo finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        Assert.That(finalizedBlockInfo.Round, Is.EqualTo(blockChain.XdcContext.CurrentRound - 3)); // Finalization is 3 rounds behind

        XdcBlockHeader? finalizedBlock = (XdcBlockHeader)blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash)!;

        TaskCompletionSource newHeadWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockChain.BlockTree.NewHeadBlock += (_, args) =>
        {
            newHeadWaitHandle.SetResult();
        };

        XdcBlockHeader forkParent = finalizedBlock;
        for (int i = 0; i < 3; i++)
        {
            //Build a fork on finalized block, which should result in fork becoming new head
            forkParent = (XdcBlockHeader)(await blockChain.AddBlockFromParent(forkParent)).Header;
        }

        if (blockChain.BlockTree.Head!.Hash != forkParent.Hash)
        {
            //Wait for new head
            await Task.WhenAny(newHeadWaitHandle.Task, Task.Delay(5_000));
        }

        Assert.That(blockChain.BlockTree.Head!.Hash, Is.EqualTo(forkParent.Hash!));
        //The new fork head should commit it's grandparent as finalized
        // The new fork head should commit it's grandparent as finalized
        Assert.That(blockChain.XdcContext.HighestCommitBlock.Hash, Is.EqualTo(blockChain.BlockTree.FindHeader(forkParent.ParentHash!)!.ParentHash!));
        //Our lock QC should be parent of the fork head
        // Our lock QC should be parent of the fork head
        Assert.That(blockChain.XdcContext.LockQC!.ProposedBlockInfo.Hash, Is.EqualTo(forkParent.ParentHash!));
    }

    [TestCase(5)]
    [TestCase(900)]
    [TestCase(901)]
    public async Task TestShouldNotReorgCommittedBlock(int number)
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create();
        ulong startRound = blockChain.XdcContext.CurrentRound;
        await blockChain.AddBlocks(number);
        BlockRoundInfo finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        Assert.That(finalizedBlockInfo.Round, Is.EqualTo(blockChain.XdcContext.CurrentRound - 3)); // Finalization is 3 rounds behind

        BlockHeader finalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash)!;
        BlockHeader? parentOfFinalizedBlock = blockChain.BlockTree.FindHeader(finalizedBlock.ParentHash!);

        Block? forkBlock = await blockChain.BlockProducer.BuildBlock(parentOfFinalizedBlock);

        Assert.That(blockChain.BlockTree.SuggestBlock(forkBlock!), Is.EqualTo(Blockchain.AddBlockResult.InvalidBlock));
    }

    [Test]
    public async Task AfterReorgSnapshotManagerReturnsSnapshotForNewChainGapBlock()
    {
        using XdcTestBlockchain blockChain = await XdcTestBlockchain.Create(3);
        blockChain.ChangeReleaseSpec(spec => { spec.EpochLength = 10; spec.Gap = 5; });
        await blockChain.AddBlocks(3);

        const ulong gapBlockNumber = 5;


        XdcBlockHeader originalChainGapBlock = (XdcBlockHeader)blockChain.BlockTree.FindHeader(gapBlockNumber)!;

        Snapshot? snapshotBeforeReorg = blockChain.SnapshotManager.GetSnapshotByGapNumber(gapBlockNumber);
        Assert.That(snapshotBeforeReorg, Is.Not.Null);
        Assert.That(snapshotBeforeReorg.HeaderHash, Is.EqualTo(originalChainGapBlock.Hash!));


        BlockRoundInfo finalizedBlockInfo = blockChain.XdcContext.HighestCommitBlock;
        Assert.That(finalizedBlockInfo.Round, Is.EqualTo(blockChain.XdcContext.CurrentRound - 3)); // Finalization is 3 rounds behind

        XdcBlockHeader finalizedBlock = (XdcBlockHeader)blockChain.BlockTree.FindHeader(finalizedBlockInfo.Hash)!;

        TaskCompletionSource newHeadWaitHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockChain.BlockTree.NewHeadBlock += (_, _) => newHeadWaitHandle.SetResult();

        XdcBlockHeader forkParent = finalizedBlock;
        for (int i = 0; i < 3; i++)
        {
            //Build a fork on finalized block, which should result in fork becoming new head
            forkParent = (XdcBlockHeader)(await blockChain.AddBlockFromParent(forkParent)).Header;
        }

        if (blockChain.BlockTree.Head!.Hash != forkParent.Hash)
            await Task.WhenAny(newHeadWaitHandle.Task, Task.Delay(5_000));

        Assert.That(blockChain.BlockTree.Head!.Hash, Is.EqualTo(forkParent.Hash!));

        XdcBlockHeader newChainGapBlock = (XdcBlockHeader)blockChain.BlockTree.FindHeader(gapBlockNumber)!;
        Snapshot? snapshotAfterReorg = blockChain.SnapshotManager.GetSnapshotByGapNumber(gapBlockNumber);
        Assert.That(snapshotAfterReorg, Is.Not.Null);
        Assert.That(snapshotAfterReorg.HeaderHash, Is.EqualTo(newChainGapBlock.Hash!));
    }
}
