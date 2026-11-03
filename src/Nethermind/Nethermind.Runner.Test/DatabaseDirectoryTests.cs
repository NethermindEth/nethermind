// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class DatabaseDirectoryTests
{
    [Test]
    public void Db_dir_is_used_as_given([Values] bool dbDirRooted, [Values] bool configuredRooted)
    {
        string dbDir = dbDirRooted ? Path.Combine(Path.GetTempPath(), "fast") : "fast";
        string configured = configuredRooted ? Path.Combine(Path.GetTempPath(), "data", "db") : Path.Combine("nethermind_db", "mainnet");
        string expected = dbDirRooted ? dbDir : Path.Combine(PathUtils.ExecutingDirectory, "fast");

        Assert.That(DatabaseDirectory.Resolve(dbDir, configured), Is.EqualTo(expected));
    }

    [Test]
    public void Configured_path_is_kept_without_db_dir([Values(null, "", " ")] string dbDir)
    {
        string configured = Path.Combine(Path.GetTempPath(), "data", "db");

        Assert.That(DatabaseDirectory.Resolve(dbDir, configured), Is.EqualTo(configured));
    }
}
