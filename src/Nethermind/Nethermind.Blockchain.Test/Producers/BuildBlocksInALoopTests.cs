// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class BuildBlocksInALoopTests
{
    [Test]
    public async Task Disposing_before_start_prevents_a_late_start_and_allows_repeated_disposal()
    {
        BuildBlocksInALoop loop = new(LimboLogs.Instance, autoStart: false);
        await loop.DisposeAsync();

        loop.StartLoop();
        await loop.DisposeAsync();
    }

    [Test]
    public async Task Concurrent_disposals_both_join_the_running_loop()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Block?> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BuildBlocksInALoop loop = new(LimboLogs.Instance, autoStart: false);
        loop.TriggerBlockProduction += (_, args) =>
        {
            args.BlockProductionTask = finish.Task;
            started.TrySetResult();
        };
        loop.StartLoop();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task first = loop.DisposeAsync().AsTask();
            Task second = loop.DisposeAsync().AsTask();
            Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        }
        finally
        {
            finish.TrySetResult(null);
            await loop.DisposeAsync();
        }
    }
}
