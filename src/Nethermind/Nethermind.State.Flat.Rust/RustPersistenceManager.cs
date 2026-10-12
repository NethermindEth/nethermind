// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// <see cref="IPersistenceManager"/> of the native snapshot manager, whose persistence worker runs
/// <see cref="AddToPersistence"/> itself after every compaction.
/// </summary>
/// <remarks>
/// A persist run through it applies the <c>IsPersisted</c> replay before returning, as <c>PersistenceManager</c> marks
/// the nodes it writes, and a drop releases the nodes the replay kept for the snapshots dropped; the history capture
/// hook is not supported, so the cancellation token of <see cref="FlushToPersistence"/>, which only that hook reads, is
/// ignored.
/// </remarks>
internal sealed unsafe class RustPersistenceManager(NativeFlatManager manager) : IPersistenceManager
{
    public IPersistence.IPersistenceReader LeaseReader(ReaderFlags flags = ReaderFlags.None) => manager.Persistence.CreateReader(flags);

    public StateId GetCurrentPersistedStateId() => manager.PersistedState();

    public Task AddToPersistence(StateId latestSnapshot)
    {
        FfiStateId latest = new(latestSnapshot);
        manager.ThrowIfFailed(NativeMethods.fdb_add_to_persistence(manager.Handle, &latest));
        manager.ProcessPersistedEvents();
        return Task.CompletedTask;
    }

    public StateId FlushToPersistence(CancellationToken cancellationToken)
    {
        FfiStateId persisted;
        manager.ThrowIfFailed(NativeMethods.fdb_flush_to_persistence(manager.Handle, &persisted));
        manager.ProcessPersistedEvents();
        return persisted.ToStateId();
    }

    public void ResetPersistedStateId() => manager.ThrowIfFailed(NativeMethods.fdb_reset_persisted(manager.Handle));

    public void DropStateNotReachableFrom(in StateId head)
    {
        FfiStateId nativeHead = new(head);
        manager.ThrowIfFailed(NativeMethods.fdb_drop_unreachable(manager.Handle, &nativeHead));
        manager.DropUnheldReplayNodes();
    }
}
