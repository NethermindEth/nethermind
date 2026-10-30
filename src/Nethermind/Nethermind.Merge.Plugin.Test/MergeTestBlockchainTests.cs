// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
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

        TaskCompletionSource processingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Block block = EngineModuleTests.OccupyBlockProcessor(chain, chain.BlockTree.Head!, processingRelease.Task);

            Task wait = chain.WaitForCommitted(block.Hash!);
            Task first = await Task.WhenAny(wait, Task.Delay(ShortTimeoutBlockchain.Timeout * 5));
            Assert.That(first, Is.SameAs(wait), "the wait must end at the test timeout instead of hanging");
            TimeoutException? timeout = await Assert.ThrowsAsync<TimeoutException>(() => wait);
            Assert.That(timeout!.Message, Does.Contain(block.Hash!.ToString()));
        }
        finally
        {
            processingRelease.TrySetResult();
        }
    }

    /// <remarks>
    /// The test timeout also bounds the fixture's own build (the wait for genesis), so it stays wide enough for a cold
    /// start on a busy runner while keeping the test short.
    /// </remarks>
    private sealed class ShortTimeoutBlockchain : BaseEngineModuleTests.MergeTestBlockchain
    {
        public const int Timeout = 5_000;

        public ShortTimeoutBlockchain() => TestTimeout = Timeout;
    }
}
