// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>The leased layers on a path through the snapshot graph, each tier oldest first; every persisted layer is older than every in-memory one.</summary>
public readonly struct PbtAssembledSnapshotResult(PbtSnapshotPooledList inMemory, PbtPersistedSnapshotList persisted) : IDisposable
{
    public PbtSnapshotPooledList InMemory { get; } = inMemory;
    public PbtPersistedSnapshotList Persisted { get; } = persisted;
    public int SnapshotCount => InMemory.Count + Persisted.Count;

    public void Dispose()
    {
        InMemory.Dispose();
        Persisted.Dispose();
    }
}
