// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Runner;

internal static class DatabaseDirectory
{
    /// <summary>
    /// Returns the database directory: <paramref name="dbDir"/> when given, otherwise <paramref name="configured"/> or the default.
    /// </summary>
    /// <remarks>
    /// <paramref name="dbDir"/> is the <c>--db-dir</c> value. It is used as given rather than as a prefix for the configured path,
    /// so it also wins over the path derived from <c>--data-dir</c>; a relative value resolves the way <c>--data-dir</c> does.
    /// </remarks>
    public static string Resolve(string? dbDir, string? configured) => string.IsNullOrWhiteSpace(dbDir)
        ? configured ?? string.Empty.GetApplicationResourcePath("db")
        : dbDir.GetApplicationResourcePath();
}
