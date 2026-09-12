// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>Persists PBT ranges before the corresponding flat ranges.</summary>
/// <remarks>
/// PBT persistence triggers are disabled so both backends use the flat persistence schedule. PBT is
/// persisted first so its failure prevents a flat-only write. The databases cannot commit atomically;
/// a crash between writes requires re-importing the PBT mirror (see <see cref="IPbtConfig.MirrorFlat"/>).
/// </remarks>
/// <param name="pbtManager">Lazily resolves the manager to avoid a dependency cycle during flat-backend selection.</param>
public class PbtFlatDrivenPersistence(IPersistence inner, Lazy<PbtDbManager> pbtManager, IPbtPersistence pbtPersistence) : IPersistence
{
    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

    public IPersistence.IWriteBatch CreateWriteBatch(in FlatStateId from, in FlatStateId to, WriteFlags flags = WriteFlags.None)
    {
        // Sync, import, and sentinel state IDs have no PBT chain.
        if (TryToPbtStateId(in to, out StateId seed)) pbtManager.Value.PersistUpTo(seed);

        return inner.CreateWriteBatch(in from, in to, flags);
    }

    /// <remarks>Also flushes PBT so a WAL-skipping flat bulk write does not leave the mirror non-durable.</remarks>
    public void Flush()
    {
        inner.Flush();
        pbtPersistence.Flush();
    }

    public void Clear() => inner.Clear();

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
