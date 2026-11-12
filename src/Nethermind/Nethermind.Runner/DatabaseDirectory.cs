// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
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
    /// <param name="logger">Receives a warning when an existing database is kept at the previous location.</param>
    /// <remarks>
    /// <c>--db-dir</c> is the parent directory of the configured path (bundled configs use <c>nethermind_db/&lt;network&gt;</c>),
    /// and a rooted configured path is kept as is. It is applied to <paramref name="configured"/> rather than to <paramref name="resolved"/>,
    /// which <c>--data-dir</c> has already made rooted, so <c>--db-dir</c> wins over the data directory.
    /// Earlier versions applied <c>--db-dir</c> to <paramref name="resolved"/>, so a node run with both options kept its database
    /// under the data directory. If that previous location holds a database and the <c>--db-dir</c> location does not,
    /// the previous location is kept and a warning is logged, so the node does not start over with an empty database.
    /// </remarks>
    public static string Resolve(string? dbDir, string? configured, string resolved, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(dbDir)) return resolved;

        string path = configured.GetApplicationResourcePath(dbDir);
        string previous = resolved.GetApplicationResourcePath(dbDir);

        if (path == previous || !HasEntries(previous) || HasEntries(path)) return path;

        if (logger.IsWarn) logger.Warn($"Keeping the existing database at {Path.GetFullPath(previous):hide} because the --db-dir location {Path.GetFullPath(path):hide} holds no database. To use the --db-dir location, stop the node and move the database there.");

        return previous;
    }

    private static bool HasEntries(string path) => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
}
