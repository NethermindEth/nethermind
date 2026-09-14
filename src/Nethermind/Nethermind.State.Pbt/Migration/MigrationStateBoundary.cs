// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Reports the persisted pointer of the backend that can be re-driven from it.</summary>
/// <remarks>
/// Before activation only flat can be re-processed onto (the follower re-drives PBT), so the head must rewind to
/// flat's pointer; after activation flat is frozen and PBT's pointer is the truth. PBT keys its states by the
/// header root, which equals its own tree root only once EIP-8347 is active, so the persisted metadata alone tells
/// which side of the activation the pointer is on — this cannot consult the block tree, whose constructor reads it.
/// </remarks>
internal sealed class MigrationStateBoundary(FlatStateBoundary flat, IPbtPersistence pbtPersistence) : IStateBoundary, IFullStateFinder
{
    public ulong? RetentionWindowBlocks => null;
    public ulong? OldestStateBlock => BestPersisted();
    public ulong? BestPersistedState => BestPersisted();
    public ulong FindBestFullState() => BestPersisted() ?? 0;

    private ulong? BestPersisted()
    {
        using IPbtPersistence.IReader reader = pbtPersistence.CreateReader();
        StateId pbt = reader.CurrentState;
        bool pbtLive = pbt != StateId.PreGenesis && pbt.StateRoot == reader.CurrentRoot;
        return pbtLive ? pbt.BlockNumber : flat.BestPersistedState;
    }
}
