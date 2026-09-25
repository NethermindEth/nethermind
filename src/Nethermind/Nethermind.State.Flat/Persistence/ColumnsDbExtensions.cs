// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

internal static class ColumnsDbExtensions
{
    /// <summary>Creates a snapshot tuned for the access pattern described by <paramref name="flags"/>.</summary>
    public static IColumnDbSnapshot<FlatDbColumns> CreateSnapshot(this IColumnsDb<FlatDbColumns> db, ReaderFlags flags) =>
        db.CreateSnapshot(sequentialReadAhead: (flags & ReaderFlags.FullScan) != 0);
}
