// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    /// <param name="flags">Forwarded to <see cref="IPersistence.CreateReader"/>; implementations may ignore it.</param>
    IPersistence.IPersistenceReader LeaseReader(ReaderFlags flags = ReaderFlags.None);
    StateId GetCurrentPersistedStateId();
    Task AddToPersistence(StateId latestSnapshot);
    StateId FlushToPersistence(CancellationToken cancellationToken);

    /// <summary>
    /// Converts the in-memory state of the committed head's ancestry to the persisted-snapshot tier, so a restart
    /// resumes from the head instead of re-executing the in-memory tail.
    /// </summary>
    /// <returns>The number of blocks converted.</returns>
    int PersistInMemoryTailForShutdown(CancellationToken cancellationToken);
    void ResetPersistedStateId();

    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/>, serialized against
    /// persistence. Used when the head is force-reset so state kept for abandoned branches is released.</summary>
    void DropStateNotReachableFrom(in StateId head);

    /// <summary>Writes on top of the persisted state, unless a persist is in progress or the persisted state has moved.</summary>
    /// <remarks>
    /// Does not wait for the persistence lock. The batch goes from <paramref name="expected"/> to itself, so
    /// <paramref name="write"/> must not change any value a reader of that state sees, as when copying state from another store.
    /// </remarks>
    /// <returns>False, writing nothing, when a persist holds the lock or the persisted state is no longer <paramref name="expected"/>.</returns>
    bool TryWriteOnPersistedState(in StateId expected, Action<IPersistence.IWriteBatch> write);
}
