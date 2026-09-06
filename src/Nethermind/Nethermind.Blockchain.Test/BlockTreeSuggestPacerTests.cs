// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockTreeSuggestPacerTests
{
    [Test]
    public void WillNotBlockIfInBatchLimit()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);

        Assert.That(pacer.WaitForQueue(1, default).IsCompleted, Is.True);
    }

    [Test]
    public void WillBlockIfBatchTooLarge()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);

        Assert.That(pacer.WaitForQueue(11, default).IsCompleted, Is.False);
    }

    [Test]
    public async Task WillNotMissHeadUpdateBeforeStartingBatch()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block initialHead = Build.A.Block.WithNumber(0).TestObject;
        Block advancedHead = Build.A.Block.WithNumber(6).TestObject;
        int headRead = 0;
        blockTree.Head.Returns(_ =>
        {
            if (Interlocked.Increment(ref headRead) == 1)
            {
                blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(advancedHead));
                return initialHead;
            }

            return advancedHead;
        });

        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);
        using CancellationTokenSource cts = new();
        Task pausedTask = pacer.WaitForPausedAsync(cts.Token);
        try
        {
            Task queueTask = pacer.WaitForQueue(11, default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(queueTask.IsCompleted, Is.True);
                Assert.That(pausedTask.IsCompleted, Is.False);
            }
        }
        finally
        {
            cts.Cancel();
        }

        Assert.That(async () => await pausedTask, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void WillOnlyUnblockOnceHeadReachHighEnough()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);

        Task waitTask = pacer.WaitForQueue(11, default);
        Assert.That(waitTask.IsCompleted, Is.False);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(1).TestObject));
        Assert.That(waitTask.IsCompleted, Is.False);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(5).TestObject));
        Assert.That(waitTask.IsCompleted, Is.False);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(Build.A.Block.WithNumber(6).TestObject));
        // Allow the async continuation (RunContinuationsAsynchronously on the TCS) to be scheduled,
        // but assert it completes promptly — the test still fails if the unblock didn't happen.
        Assert.That(waitTask.Wait(TimeSpan.FromMilliseconds(500)), Is.True);
    }
}
