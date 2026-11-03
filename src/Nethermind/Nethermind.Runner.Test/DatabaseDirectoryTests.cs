// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class DatabaseDirectoryTests
{
    private static readonly string Configured = Path.Combine("nethermind_db", "mainnet");
    private static readonly string DataDir = Path.Combine(Path.GetTempPath(), "data");

    [Test]
    public void Db_dir_is_the_parent_of_the_configured_path([Values] bool dbDirRooted, [Values] bool dataDirSet)
    {
        string dbDir = dbDirRooted ? Path.Combine(Path.GetTempPath(), "fast") : "fast";
        string resolved = dataDirSet ? Path.Combine(DataDir, Configured) : Configured;
        string expected = Path.Combine(dbDirRooted ? dbDir : Path.Combine(PathUtils.ExecutingDirectory, dbDir), Configured);

        Assert.That(DatabaseDirectory.Resolve(dbDir, Configured, resolved), Is.EqualTo(expected));
    }

    [Test]
    public void Rooted_configured_path_is_kept_with_db_dir()
    {
        string configured = Path.Combine(Path.GetTempPath(), "db");

        Assert.That(DatabaseDirectory.Resolve("fast", configured, configured), Is.EqualTo(configured));
    }

    [Test]
    public void Resolved_path_is_kept_without_db_dir([Values(null, "", " ")] string dbDir, [Values] bool dataDirSet)
    {
        string resolved = dataDirSet ? Path.Combine(DataDir, Configured) : Configured;

        Assert.That(DatabaseDirectory.Resolve(dbDir, Configured, resolved), Is.EqualTo(resolved));
    }
}
