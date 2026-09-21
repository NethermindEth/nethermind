// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IPersistenceManager
{
    IPersistence.IPersistenceReader LeaseReader();
    StateId GetCurrentPersistedStateId();
    Task AddToPersistence(StateId latestSnapshot);
    StateId FlushToPersistence(CancellationToken cancellationToken);
    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/>, serialized against
    /// persistence. Used when the head is force-reset so state kept for abandoned branches is released.</summary>
    void DropStateNotReachableFrom(in StateId head);

    /// <summary>Runs <paramref name="work"/> against a sync-flagged write batch under the persistence lock, so its writes never
    /// interleave with a snapshot persist. Returns <c>false</c> without running it while a state sync is writing.</summary>
    /// <remarks>The batch commits when it is disposed, also when <paramref name="work"/> throws: what it staged before the
    /// throw lands. Callers stage writes that are correct on their own, such as idempotent range deletes.</remarks>
    bool RunMaintenance(Action<IPersistence.IWriteBatch> work, CancellationToken cancellationToken);

    /// <summary>Whether a state sync has announced writes that have not ended yet; maintenance is refused meanwhile.</summary>
    bool StateSyncWriting { get; }

    /// <summary>Announces that a state sync is about to write, so maintenance is refused until <see cref="EndStateSync"/>.</summary>
    void BeginStateSync();

    /// <summary>Clears the persisted base for a state sync that starts over, and announces the sync's writes.</summary>
    void ClearForStateSync();

    /// <summary>Ends the announced sync: re-reads the persisted state pointer through a sync reader and allows maintenance again.
    /// A sync that is abandoned without this call is ended by the next real persist.</summary>
    void EndStateSync();
}
