// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

/// <summary>
/// Flat backend's <see cref="IStateBoundary"/>: both the floor and the best-persisted-state
/// ceiling read straight from the persisted <c>CurrentState</c> in the flat metadata column.
/// </summary>
/// <remarks>
/// Sits on <see cref="IPersistence"/> rather than <see cref="IPersistenceManager"/> so it can be
/// injected into the block tree's constructor: the manager's graph resolves the finalized-state
/// provider, which resolves the block tree.
/// </remarks>
public class FlatStateBoundary(IPersistence persistence) : IStateBoundary
{
    public ulong? RetentionWindowBlocks => null;

    public ulong? OldestStateBlock => CurrentPersistedBlock();

    public ulong? BestPersistedState => CurrentPersistedBlock();

    private ulong? CurrentPersistedBlock()
    {
        ulong blockNumber = persistence.GetCurrentState().BlockNumber;
        return blockNumber != ulong.MaxValue ? blockNumber : null;
    }
}
