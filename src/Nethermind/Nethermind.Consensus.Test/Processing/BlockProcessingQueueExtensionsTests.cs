// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class BlockProcessingQueueExtensionsTests
{
    private static readonly Hash256 Hash = TestItem.KeccakA;

    [Test]
    public async Task Waiting_for_processing_handles_queue_draining_during_subscription()
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        queue.IsEmpty.Returns(false, true);

        await queue.WaitForBlockProcessing().WaitAsync(TimeSpan.FromSeconds(2));

        queue.Received().ProcessingQueueEmpty -= Arg.Any<EventHandler>();
    }

    [Test]
    public async Task WaitForExecutedCopy_NothingCommitting_ReturnsAtOnce()
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        queue.WaitUntilExecutedCopyRemovedAsync(Hash).Returns(ValueTask.CompletedTask);

        ValueTask<bool> wait = queue.WaitForExecutedCopyAsync(Hash, TimeSpan.FromMinutes(1));

        Assert.That(wait.IsCompletedSuccessfully, Is.True, "no copy is committing, so there is nothing to wait for");
        Assert.That(await wait, Is.True);
    }

    [Test, MaxTime(10_000)]
    public async Task WaitForExecutedCopy_CommitLands_ReturnsTrue()
    {
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        queue.WaitUntilExecutedCopyRemovedAsync(Hash).Returns(new ValueTask(committed.Task));

        ValueTask<bool> wait = queue.WaitForExecutedCopyAsync(Hash, TimeSpan.FromMinutes(1));
        Assert.That(wait.IsCompleted, Is.False, "precondition: the copy is still committing");
        committed.SetResult();

        Assert.That(await wait, Is.True);
    }

    [Test, MaxTime(10_000)]
    public async Task WaitForExecutedCopy_CommitOutlastsTheBound_ReturnsFalse()
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        queue.WaitUntilExecutedCopyRemovedAsync(Hash).Returns(new ValueTask(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task));

        Assert.That(await queue.WaitForExecutedCopyAsync(Hash, TimeSpan.FromMilliseconds(50)), Is.False);
    }
}
