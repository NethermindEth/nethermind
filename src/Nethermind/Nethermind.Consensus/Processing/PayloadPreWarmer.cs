// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Experimental: starts a speculative pre-warm session on a payload's own transactions the moment newPayload is
/// received, so the block's head transactions are warmed during the validation window rather than raced by the main
/// thread once processing begins.
/// </summary>
/// <remarks>
/// Bridges the singleton engine handler to the scoped <see cref="IBlockCachePreWarmer"/>: a
/// <see cref="PayloadPreWarmerBinder"/> hands over the main processing scope's prewarmer when that scope activates.
/// Until then, and whenever pre-warming is off, <see cref="StartForPayload"/> is a no-op.
/// </remarks>
public sealed class PayloadPreWarmer : IDisposable
{
    // Re-warm every pass so senders recovered during the validation window are picked up as they land. The session is
    // short-lived: it is cancelled the moment the block enters processing (a consumer scope opens) or a newer payload
    // starts its own, so at most a pass or two runs before the handoff.
    private const int PassDelayMs = 16;

    private readonly CancellationTokenSource _cts = new();
    private IBlockCachePreWarmer? _preWarmer;
    private long _generation;

    /// <summary>Binds the main processing scope's prewarmer; called once when that scope activates.</summary>
    public void Bind(IBlockCachePreWarmer preWarmer) => _preWarmer = preWarmer;

    /// <summary>
    /// Warms <paramref name="block"/> against <paramref name="parent"/> until it enters processing or a newer payload
    /// arrives. The warmed caches are handed to the reactive pass through the prewarmer's own marker.
    /// </summary>
    public void StartForPayload(BlockHeader parent, Block block, IReleaseSpec spec)
    {
        IBlockCachePreWarmer? preWarmer = _preWarmer;
        if (preWarmer is null) return;

        long generation = Interlocked.Increment(ref _generation);
        preWarmer.StartSpeculativePreWarm(
            parent,
            spec,
            generation,
            token => token.IsCancellationRequested ? null : (block, spec),
            PassDelayMs,
            _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>
/// Experimental binder: resolved when the main processing scope's <see cref="IBlockCachePreWarmer"/> activates, it
/// hands that scoped instance to the singleton <see cref="PayloadPreWarmer"/> the engine handler holds.
/// </summary>
public sealed class PayloadPreWarmerBinder
{
    public PayloadPreWarmerBinder(PayloadPreWarmer dispatcher, IBlockCachePreWarmer preWarmer) => dispatcher.Bind(preWarmer);
}
