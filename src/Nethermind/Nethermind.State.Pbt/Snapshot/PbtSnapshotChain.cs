// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

public sealed class PbtRetainedPublicationGate
{
    public Lock Sync { get; } = new();
}

public sealed class PbtRetainedStorageLifetime
{
    private IArenaManager? Arena { get; }
    private BlobArenaManager? Blobs { get; }
    public PbtRetainedStorageLifetime() { }
    public PbtRetainedStorageLifetime(IArenaManager arena, BlobArenaManager blobs) => (Arena, Blobs) = (arena, blobs);

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
    public PbtRetainedSnapshot? Retained { get; }
    public StateId From => Memory?.From ?? Retained!.From;
    public StateId To => Memory?.To ?? Retained!.To;
    public ValueHash256 TreeRoot => Memory?.TreeRoot ?? Retained!.TreeRoot;
    public SnapshotTier Tier { get; }

    public PbtSnapshotLease(PbtSnapshot snapshot, SnapshotTier tier) => (Memory, Tier) = (snapshot, tier);
    public PbtSnapshotLease(PbtRetainedSnapshot snapshot) => (Retained, Tier) = (snapshot, snapshot.Tier);

    public PbtSnapshotLease Lease()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Memory is { } memory)
        {
            if (!memory.TryLease()) throw new ObjectDisposedException(nameof(memory));
            return new(memory, Tier);
        }
        if (!Retained!.TryLease()) throw new ObjectDisposedException(nameof(Retained));
        return new(Retained);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Memory?.Dispose();
        Retained?.Dispose();
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
