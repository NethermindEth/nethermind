// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Migration;

/// <summary>The <see cref="MigrationCursor"/> of the HalfPath to flat migration in progress, shared by the readers that route by it and the copier that advances it.</summary>
public sealed class HalfPathMigrationCursor(IColumnsDb<FlatDbColumns> db)
{
    // Held from a chunk's check of the cursor to its publication, so an abandonment waits for a chunk being committed.
    private readonly Lock _lock = new();

    // MigrationCursor is too large to read and write atomically, so it is published boxed.
    private StrongBox<MigrationCursor>? _current =
        BasePersistence.ReadMigrationCursor(db.GetColumnDb(FlatDbColumns.Metadata)) is { } stored ? new(stored) : null;

    /// <summary>The cursor to route reads by, or <c>null</c> when no migration is in progress.</summary>
    /// <remarks>Read before a reader takes its snapshot, it never covers data the snapshot lacks: it moves only after the data is committed.</remarks>
    public MigrationCursor? Current => Volatile.Read(ref _current)?.Value;

    /// <summary>Commits a chunk copied from <paramref name="from"/> and publishes <paramref name="next"/>, unless the cursor is no longer <paramref name="from"/>.</summary>
    /// <param name="from">The cursor the chunk was copied from.</param>
    /// <param name="next">The cursor the chunk saves with the data it covers; <c>null</c> once everything is copied, so reads stop falling back.</param>
    /// <param name="commit">Writes and commits the chunk, returning whether it did.</param>
    /// <returns>Whether the chunk was committed.</returns>
    public bool TryAdvance(in MigrationCursor from, in MigrationCursor? next, Func<bool> commit)
    {
        using Lock.Scope _ = _lock.EnterScope();
        if (Current != from || !commit()) return false;

        Volatile.Write(ref _current, next is { } position ? new StrongBox<MigrationCursor>(position) : null);
        return true;
    }

    /// <summary>Stops routing reads by the cursor and advancing it, ahead of a wipe that deletes it.</summary>
    /// <remarks>Waits for a chunk being committed, so the wipe deletes that chunk too.</remarks>
    public void Abandon()
    {
        using Lock.Scope _ = _lock.EnterScope();
        Volatile.Write(ref _current, null);
    }
}
