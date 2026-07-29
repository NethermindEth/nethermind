// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>
/// PBT backend's <see cref="IStateBoundary"/>: both the floor and the best-persisted-state
/// ceiling read straight from the persisted <c>CurrentState</c> in the pbt metadata column.
/// </summary>
/// <remarks>
/// Sits on <see cref="IPbtPersistence"/> rather than <see cref="IPbtDbManager"/> so it can be
/// injected into the block tree's constructor without resolving the manager's graph back into
/// the block tree.
/// </remarks>
public class PbtStateBoundary(IPbtPersistence persistence) : IStateBoundary
{
    public ulong? RetentionWindowBlocks => null;

    public ulong? OldestStateBlock => CurrentPersistedBlock();

    public ulong? BestPersistedState => CurrentPersistedBlock();

    private ulong? CurrentPersistedBlock()
    {
        using IPbtPersistence.IReader reader = persistence.CreateReader();
        ulong blockNumber = reader.CurrentState.BlockNumber;
        return blockNumber != ulong.MaxValue ? blockNumber : null;
    }
}
