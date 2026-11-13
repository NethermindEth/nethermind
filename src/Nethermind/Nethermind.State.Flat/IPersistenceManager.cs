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

    /// <summary>
    /// Compacts the persisted snapshots the last run left one per block, so reads after a restart do not walk each
    /// of them. For the start only, before anything else uses the persisted tier.
    /// </summary>
    /// <returns>The number of compacted snapshots written.</returns>
    int CompactReloadedSnapshots(CancellationToken cancellationToken);

    void ResetPersistedStateId();

    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/>, serialized against
    /// persistence. Used when the head is force-reset so state kept for abandoned branches is released.</summary>
    void DropStateNotReachableFrom(in StateId head);
}
