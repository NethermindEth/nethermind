// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Lets a request that has a block for main processing start that processing's warming before the block is queued.
/// </summary>
/// <remarks>
/// The prewarmer is scoped to main processing, outside the reach of the engine API's handlers; it attaches itself here
/// when main processing resolves it. Until then, and wherever nothing attaches, every call is a no-op.
/// </remarks>
public sealed class EarlyBlockPreWarming
{
    private IBlockCachePreWarmer? _preWarmer;

    /// <summary>Called for the main processing's prewarmer; any later one is ignored.</summary>
    public void Attach(IBlockCachePreWarmer preWarmer) => Interlocked.CompareExchange(ref _preWarmer, preWarmer, null);

    /// <inheritdoc cref="IBlockCachePreWarmer.StartEarly"/>
    public void Start(Block block, BlockHeader parent, IReleaseSpec spec) => Volatile.Read(ref _preWarmer)?.StartEarly(block, parent, spec);

    /// <inheritdoc cref="IBlockCachePreWarmer.DiscardEarly"/>
    public void Discard(Block block) => Volatile.Read(ref _preWarmer)?.DiscardEarly(block);
}
