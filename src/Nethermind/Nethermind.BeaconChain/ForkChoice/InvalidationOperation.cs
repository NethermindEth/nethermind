// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>An operation which may invalidate the execution status of some proto-array nodes.</summary>
/// <remarks>Mirrors Lighthouse's <c>InvalidationOperation</c> enum (<c>InvalidateOne</c> / <c>InvalidateMany</c>).</remarks>
public sealed class InvalidationOperation
{
    private InvalidationOperation(Hash256 headBlockRoot, bool invalidateBlockRoot, Hash256? latestValidAncestor)
    {
        HeadBlockRoot = headBlockRoot;
        InvalidateBlockRoot = invalidateBlockRoot;
        LatestValidAncestor = latestValidAncestor;
    }

    public Hash256 HeadBlockRoot { get; }

    /// <summary>Whether <see cref="HeadBlockRoot"/> itself is invalidated when <see cref="LatestValidAncestor"/> is not a known ancestor.</summary>
    public bool InvalidateBlockRoot { get; }

    /// <summary>The execution block hash of the latest valid ancestor, if reported by the execution layer.</summary>
    public Hash256? LatestValidAncestor { get; }

    /// <summary>Invalidate only <paramref name="blockRoot"/> and its descendants; never its ancestors.</summary>
    public static InvalidationOperation InvalidateOne(Hash256 blockRoot) => new(blockRoot, true, null);

    /// <summary>
    /// Invalidate all blocks between <paramref name="headBlockRoot"/> (inclusive) and the block whose
    /// execution payload is <paramref name="latestValidAncestor"/> (exclusive), plus their descendants.
    /// </summary>
    /// <remarks>
    /// If <paramref name="latestValidAncestor"/> is not known to fork choice, only
    /// <paramref name="headBlockRoot"/> is invalidated, and only when <paramref name="alwaysInvalidateHead"/> is set.
    /// </remarks>
    public static InvalidationOperation InvalidateMany(Hash256 headBlockRoot, bool alwaysInvalidateHead, Hash256 latestValidAncestor) =>
        new(headBlockRoot, alwaysInvalidateHead, latestValidAncestor);
}
