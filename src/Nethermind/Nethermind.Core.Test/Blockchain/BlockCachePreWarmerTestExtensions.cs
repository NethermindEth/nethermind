// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Core.Test.Blockchain;

/// <summary>Test-only helpers for driving <see cref="IBlockCachePreWarmer"/> speculative warming.</summary>
public static class BlockCachePreWarmerTestExtensions
{
    /// <summary>
    /// Runs one speculative warming pass over <paramref name="delta"/> and joins it, leaving the published handoff
    /// marker for the next <c>PreWarmCaches</c> call to consume.
    /// </summary>
    /// <param name="preWarmer">The prewarmer to drive.</param>
    /// <param name="head">The head to warm against; the marker records it as the handoff parent.</param>
    /// <param name="spec">The spec the session runs under; a handoff requires the consuming block's spec to match it by reference.</param>
    /// <param name="delta">The block whose transactions are warmed. The pass is fed this once, then nothing.</param>
    /// <param name="markerPublished">
    /// Probe for the prewarmer's marker flag, passed in because it is internal to <c>Nethermind.Consensus</c> and visible
    /// to the calling test assemblies but not to this one.
    /// </param>
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

        bool published = false;
        try
        {
            published = SpinWait.SpinUntil(markerPublished, TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            task.GetAwaiter().GetResult();
        }

        Assert.That(published, Is.True, "precondition: speculative warming must publish a handoff marker");
    }
}
