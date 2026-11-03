// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Migration;

/// <summary>The <see cref="MigrationCursor"/> of the HalfPath to flat migration in progress, shared by the readers that route by it and the copier that advances it.</summary>
public sealed class HalfPathMigrationCursor(IColumnsDb<FlatDbColumns> db)
{
    private readonly IDb _metadata = db.GetColumnDb(FlatDbColumns.Metadata);

    // MigrationCursor is too large to read and write atomically, so it is published boxed.
    private StrongBox<MigrationCursor>? _current =
        BasePersistence.ReadMigrationCursor(db.GetColumnDb(FlatDbColumns.Metadata)) is { } stored ? new(stored) : null;

    /// <summary>The cursor to route reads by, or <c>null</c> when no migration is in progress.</summary>
    /// <remarks>Read before a reader takes its snapshot, it never covers data the snapshot lacks: it moves only after the data is committed.</remarks>
    public MigrationCursor? Current => Volatile.Read(ref _current)?.Value;

    /// <summary>Saves and publishes <paramref name="cursor"/>, once the data it covers is committed.</summary>
    public void Advance(in MigrationCursor cursor)
    {
        BasePersistence.WriteMigrationCursor(_metadata, cursor);
        Volatile.Write(ref _current, new StrongBox<MigrationCursor>(cursor));
    }

    /// <summary>Ends the migration once everything is copied, so reads stop falling back.</summary>
    public void Complete()
    {
        BasePersistence.WriteMigrationCursor(_metadata, null);
        Volatile.Write(ref _current, null);
    }

    /// <summary>Stops routing reads by the cursor after a wipe has deleted it.</summary>
    public void Abandon() => Volatile.Write(ref _current, null);
}
