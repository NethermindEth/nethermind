// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Runner;

internal static class DatabaseDirectory
{
    /// <summary>
    /// Returns the database path: <paramref name="configured"/> under <paramref name="dbDir"/> when given, otherwise <paramref name="resolved"/>.
    /// </summary>
    /// <param name="dbDir">The <c>--db-dir</c> value.</param>
    /// <param name="configured">The configured <c>Init.BaseDbPath</c>, before <c>--data-dir</c> is applied.</param>
    /// <param name="resolved">The database path after <c>--data-dir</c> is applied.</param>
    /// <remarks>
    /// <c>--db-dir</c> is the parent directory of the configured path (bundled configs use <c>nethermind_db/&lt;network&gt;</c>),
    /// and a rooted configured path is kept as is. It is applied to <paramref name="configured"/> rather than to <paramref name="resolved"/>,
    /// which <c>--data-dir</c> has already made rooted, so <c>--db-dir</c> wins over the data directory.
    /// </remarks>
    public static string Resolve(string? dbDir, string? configured, string resolved) =>
        string.IsNullOrWhiteSpace(dbDir) ? resolved : configured.GetApplicationResourcePath(dbDir);
}
