// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class MergeTestBlockchainTests
{
    [Test]
    public async Task WaitForCommitted_fails_naming_the_block_when_it_stays_in_the_processing_queue()
    {
        using BaseEngineModuleTests.MergeTestBlockchain chain =
            await new ShortTimeoutBlockchain().Build(new TestSingleReleaseSpecProvider(London.Instance));
        Block head = chain.BlockTree.Head!;
        Block block = Build.A.Block.WithNumber(head.Number + 1).WithParent(head)
            .WithNonce(0).WithDifficulty(0).WithStateRoot(head.StateRoot!).TestObject;
        block.Header.TotalDifficulty = head.TotalDifficulty;

        TestBranchProcessorInterceptor branchProcessor = (TestBranchProcessorInterceptor)chain.BranchProcessor;
        TaskCompletionSource processingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim processingStarted = new(false);
        branchProcessor.ProcessingStarted = processingStarted;
        branchProcessor.ProcessingRelease = processingRelease.Task;
        try
        {
            _ = Task.Run(async () => await chain.BlockProcessingQueue.Enqueue(
                block, ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotUpdateHead));
            Assert.That(processingStarted.Wait(TimeSpan.FromSeconds(5)), Is.True, "the block never reached the processor");

            Task wait = chain.WaitForCommitted(block.Hash!);
            Task first = await Task.WhenAny(wait, Task.Delay(ShortTimeoutBlockchain.Timeout * 5));
            Assert.That(first, Is.SameAs(wait), "the wait must end at the test timeout instead of hanging");
            TimeoutException? timeout = await Assert.ThrowsAsync<TimeoutException>(() => wait);
            Assert.That(timeout!.Message, Does.Contain(block.Hash!.ToString()));
        }
        finally
        {
            branchProcessor.ProcessingStarted = null;
            processingRelease.TrySetResult();
        }
    }

    private sealed class ShortTimeoutBlockchain : BaseEngineModuleTests.MergeTestBlockchain
    {
        public const int Timeout = 2_000;

        public ShortTimeoutBlockchain() => TestTimeout = Timeout;
    }
}
