// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

public sealed class PbtPersistedPublicationGate
{
    public Lock Sync { get; } = new();
}

public sealed class PbtPersistedStorageLifetime
{
    private IArenaManager? Arena { get; }
    private BlobArenaManager? Blobs { get; }
    public PbtPersistedStorageLifetime() { }
    public PbtPersistedStorageLifetime(IArenaManager arena, BlobArenaManager blobs) => (Arena, Blobs) = (arena, blobs);

    public void AbortLoad()
    {
        if (Arena is ArenaManager arena) arena.PreserveFilesOnFailure();
        Blobs?.PreserveFilesOnFailure();
    }
}

public sealed class PbtSnapshotLease : IDisposable
{
    private int _disposed;
    public PbtSnapshot? Memory { get; }
    public PbtPersistedSnapshot? Persisted { get; }
    public StateId From => Memory?.From ?? Persisted!.From;
    public StateId To => Memory?.To ?? Persisted!.To;
    public ValueHash256 TreeRoot => Memory?.TreeRoot ?? Persisted!.TreeRoot;
    public SnapshotTier Tier { get; }

    public PbtSnapshotLease(PbtSnapshot snapshot, SnapshotTier tier) => (Memory, Tier) = (snapshot, tier);
    public PbtSnapshotLease(PbtPersistedSnapshot snapshot) => (Persisted, Tier) = (snapshot, snapshot.Tier);

    public PbtSnapshotLease Lease()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Memory is { } memory)
        {
            if (!memory.TryLease()) throw new ObjectDisposedException(nameof(memory));
            return new(memory, Tier);
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

public sealed class PbtSnapshotChain(List<PbtSnapshotLease> layers) : IDisposable
{
    private List<PbtSnapshotLease>? _layers = layers;
    public IReadOnlyList<PbtSnapshotLease> Layers => _layers ?? throw new ObjectDisposedException(nameof(PbtSnapshotChain));
    public void Dispose()
    {
        List<PbtSnapshotLease>? layers = Interlocked.Exchange(ref _layers, null);
        if (layers is null) return;
        foreach (PbtSnapshotLease layer in layers) layer.Dispose();
    }
}
