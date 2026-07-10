// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat;

public readonly struct AssembledSnapshotResult(SnapshotPooledList inMemory, PersistedSnapshotList persisted) : IDisposable
{
    public SnapshotPooledList InMemory { get; } = inMemory;
    public PersistedSnapshotList Persisted { get; } = persisted;
    public readonly int SnapshotCount => InMemory.Count + Persisted.Count;

    public readonly void Dispose()
    {
        InMemory.Dispose();
        Persisted.Dispose();
    }
}
