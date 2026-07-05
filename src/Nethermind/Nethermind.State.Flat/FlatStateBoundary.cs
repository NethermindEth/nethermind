// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
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
    private CachedRoot? _cached;

    public ulong? RetentionWindowBlocks => null;

    public ulong? OldestStateBlock => CurrentPersistedBlock();

    public ulong? BestPersistedState => CurrentPersistedBlock();

    public bool TryGetBestPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot)
    {
        StateId current = CurrentPersistedState();
        if (current == StateId.PreGenesis || current == StateId.Sync)
        {
            blockNumber = 0;
            stateRoot = null;
            return false;
        }

        CachedRoot? cached = Volatile.Read(ref _cached);
        if (cached is null || cached.Id != current)
        {
            cached = new CachedRoot(current, current.StateRoot.ToCommitment());
            Volatile.Write(ref _cached, cached);
        }

        blockNumber = current.BlockNumber;
        stateRoot = cached.Root;
        return true;
    }

    private ulong? CurrentPersistedBlock()
    {
        ulong blockNumber = CurrentPersistedState().BlockNumber;
        return blockNumber != ulong.MaxValue ? blockNumber : null;
    }

    private StateId CurrentPersistedState()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return reader.CurrentState;
    }

    private sealed record CachedRoot(StateId Id, Hash256 Root);
}
