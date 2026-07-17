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
    public async Task WaitForPausedAsync_WillCompleteWhenPacerStopsSuggesting()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);
        using CancellationTokenSource waitCts = new(TimeSpan.FromSeconds(5));
        using CancellationTokenSource queueCts = new();

        Task pausedTask = pacer.WaitForPausedAsync();
        Assert.That(pausedTask.IsCompleted, Is.False);

        Task queueTask = pacer.WaitForQueue(11, queueCts.Token);

        await pausedTask.WaitAsync(waitCts.Token);
        Assert.That(queueTask.IsCompleted, Is.False);

        queueCts.Cancel();
        Assert.That(async () => await queueTask, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task WillNotMissHeadUpdateWhenStartingBatch()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        Block currentHead = Build.A.Block.WithNumber(0).TestObject;
        TaskCompletionSource headRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHeadRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstHeadRead = 1;
        blockTree.Head.Returns(_ =>
        {
            Block snapshot = Volatile.Read(ref currentHead);
            if (Interlocked.Exchange(ref firstHeadRead, 0) != 0)
            {
                headRead.TrySetResult();
                releaseHeadRead.Task.GetAwaiter().GetResult();
            }

            return snapshot;
        });

        TaskCompletionSource headEventStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHeadEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockTree.NewHeadBlock += (_, _) =>
        {
            headEventStarted.TrySetResult();
            releaseHeadEvent.Task.GetAwaiter().GetResult();
        };

        using BlockTreeSuggestPacer pacer = new(blockTree, 10, 5);
        TaskCompletionSource headEventHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        blockTree.NewHeadBlock += (_, _) => headEventHandled.TrySetResult();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        Task queueTask = Task.Run(() => pacer.WaitForQueue(11, cts.Token));
        try
        {
            await headRead.Task.WaitAsync(cts.Token);
            Volatile.Write(ref currentHead, Build.A.Block.WithNumber(6).TestObject);
            Task headEventTask = Task.Run(() => blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(currentHead)));
            await headEventStarted.Task.WaitAsync(cts.Token);

            releaseHeadEvent.TrySetResult();
            await Task.Delay(100, cts.Token);
            Assert.That(headEventHandled.Task.IsCompleted, Is.False);

            releaseHeadRead.TrySetResult();

            await headEventTask.WaitAsync(cts.Token);
            await queueTask.WaitAsync(cts.Token);
        }
        finally
        {
            releaseHeadEvent.TrySetResult();
            releaseHeadRead.TrySetResult();
            cts.Cancel();
        }
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
