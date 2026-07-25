// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Core.Test.Blockchain;

public static class BlockCachePreWarmerTestExtensions
{
    public static void RunSpeculativePreWarm(
        this IBlockCachePreWarmer preWarmer,
        BlockHeader head,
        IReleaseSpec spec,
        Block delta,
        Func<bool> markerPublished)
    {
        int calls = 0;
        using CancellationTokenSource cancellation = new();
        Task task = preWarmer.StartSpeculativePreWarm(
            head,
            spec,
            generation: 1,
            _ => Interlocked.Increment(ref calls) == 1 ? delta : null,
            idlePassDelayMs: 5,
            cancellation.Token);

        bool published;
        try
        {
            published = SpinWait.SpinUntil(markerPublished, TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            task.GetAwaiter().GetResult();
        }

        // Asserted after the join so a faulted session cannot mask the timeout.
        Assert.That(published, Is.True, "precondition: speculative warming must publish a handoff marker");
    }
}
