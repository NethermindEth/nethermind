// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

public sealed class PbtPersistedPublicationGate
{
    public Lock Sync { get; } = new();
}

public sealed class PbtSnapshotLease : IDisposable
{
    private int _disposed;
    public PbtSnapshot? Memory { get; }
    public PbtPersistedSnapshot? Persisted { get; }
    public StateId From => Memory?.From ?? Persisted!.From;
    public StateId To => Memory?.To ?? Persisted!.To;
    public ValueHash256 TreeRoot => Memory?.TreeRoot ?? Persisted!.TreeRoot;

    public PbtSnapshotLease(PbtSnapshot snapshot) => Memory = snapshot;
    public PbtSnapshotLease(PbtPersistedSnapshot snapshot) => Persisted = snapshot;

    public PbtSnapshotLease Lease()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Memory is { } memory)
        {
            if (!memory.TryLease()) throw new ObjectDisposedException(nameof(memory));
            return new(memory);
        }
        if (!Persisted!.TryLease()) throw new ObjectDisposedException(nameof(Persisted));
        return new(Persisted);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Memory?.Dispose();
        Persisted?.Dispose();
    }
}
