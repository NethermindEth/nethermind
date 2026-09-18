// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// The state system's single integration point with the block tree: resolves a target block's
/// parent header, and exposes the state-pruning finality boundary and the canonical header at
/// finalized heights.
/// </summary>
public interface IStateHeaderProvider
{
    /// <summary>Finds the parent header of <paramref name="target"/>, or <c>null</c> when it is unavailable.</summary>
    BlockHeader? FindParentHeader(BlockHeader target);

    /// <summary>
    /// The highest block number at or below which state is finalized and safe to persist and prune.
    /// State above it must be kept for reorg safety.
    /// </summary>
    ulong FinalizedBlockNumber { get; }

    /// <summary>
    /// Canonical header at the given height, or <c>null</c> when the height is above
    /// <see cref="FinalizedBlockNumber"/> or the header is unavailable.
    /// </summary>
    BlockHeader? GetFinalizedHeader(ulong blockNumber);
}
