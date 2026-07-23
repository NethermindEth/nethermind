// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>
/// Decorates the flat backend's persistence so that every range it writes is first written to PBT,
/// making the flat schedule the only clock both backends run on.
/// </summary>
/// <remarks>
/// Mirroring keeps the two states equal block by block, but not on disk: they are separate databases
/// on separate schedules, so left alone their persisted pointers would drift apart and a restart would
/// find no state both could serve. PBT's own triggers are therefore switched off (see
/// <see cref="PbtPersistenceCoordinator.CheckPersistence"/>) and this drives it instead.
/// <para>
/// PBT goes first so a failure there aborts the flat write and the pair is retried on the next block
/// rather than left split. The two databases still cannot commit atomically, so a crash between them
/// splits the pointers for good; recovery is a re-import (see <see cref="IPbtConfig.MirrorFlat"/>).
/// </para>
/// </remarks>
/// <param name="pbtManager">
/// Resolved on first write rather than on construction: the flat backend decides whether it is even
/// active by reading this persistence, and the manager's graph reaches the block tree, which needs
/// that decision. Only writes need the manager, and none arrive until long after the decision.
/// </param>
public class PbtFlatDrivenPersistence(IPersistence inner, Lazy<PbtDbManager> pbtManager, IPbtPersistence pbtPersistence) : IPersistence
{
    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

    public IPersistence.IWriteBatch CreateWriteBatch(in FlatStateId from, in FlatStateId to, WriteFlags flags = WriteFlags.None)
    {
        // Writes PBT does not have a chain for — sync, import, the sentinel ids — are a no-op there,
        // which is what keeps this harmless outside block processing.
        if (TryToPbtStateId(in to, out StateId seed)) pbtManager.Value.PersistUpTo(seed);

        return inner.CreateWriteBatch(in from, in to, flags);
    }

    /// <remarks>Flushes PBT too, so a WAL-skipping flat bulk write does not leave the mirrored state non-durable behind it.</remarks>
    public void Flush()
    {
        inner.Flush();
        pbtPersistence.Flush();
    }

    public void Clear() => inner.Clear();

    /// <summary>
    /// Maps a flat state id onto the PBT one for the same state, which is possible because both key a
    /// state by its block number and the root its header claims.
    /// </summary>
    /// <returns>False for the flat sentinels, which name no state PBT could hold.</returns>
    private static bool TryToPbtStateId(in FlatStateId flatStateId, out StateId stateId)
    {
        if (flatStateId == FlatStateId.PreGenesis || flatStateId == FlatStateId.Sync)
        {
            stateId = StateId.PreGenesis;
            return false;
        }

        stateId = new StateId(flatStateId.BlockNumber, flatStateId.StateRoot);
        return true;
    }
}
