// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class DatabaseDirectoryTests
{
    private static readonly string Configured = Path.Combine("nethermind_db", "mainnet");

    private TempPath _root;
    private TestLogger _logger;

    private string DataDir => Path.Combine(_root.Path, "data");
    private string FastDir => Path.Combine(_root.Path, "fast");
    private string DataDirDbPath => Path.Combine(DataDir, Configured);
    private string FastDirDbPath => Path.Combine(FastDir, Configured);

    [SetUp]
    public void SetUp()
    {
        _root = TempPath.GetTempDirectory();
        _logger = new TestLogger();
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void Db_dir_is_the_parent_of_the_configured_path([Values] bool dbDirRooted, [Values] bool dataDirSet)
    {
        string dbDir = dbDirRooted ? FastDir : "fast";
        string resolved = dataDirSet ? DataDirDbPath : Configured;
        string expected = Path.Combine(dbDirRooted ? dbDir : Path.Combine(PathUtils.ExecutingDirectory, dbDir), Configured);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Resolve(dbDir, Configured, resolved), Is.EqualTo(expected));
            Assert.That(_logger.LogList, Is.Empty);
        }
    }

    [Test]
    public void Rooted_configured_path_is_kept_with_db_dir()
    {
        string configured = Path.Combine(_root.Path, "db");

        Assert.That(Resolve("fast", configured, configured), Is.EqualTo(configured));
    }

    [Test]
    public void Resolved_path_is_kept_without_db_dir([Values(null, "", " ")] string dbDir, [Values] bool dataDirSet)
    {
        string resolved = dataDirSet ? DataDirDbPath : Configured;

        Assert.That(Resolve(dbDir, Configured, resolved), Is.EqualTo(resolved));
    }

    [Test]
    public void Existing_database_under_data_dir_is_kept_when_db_dir_holds_none([Values] bool dbDirLocationExists)
    {
        // A node run before --db-dir took precedence over --data-dir has its database under the data directory.
        CreateDatabase(DataDirDbPath);
        if (dbDirLocationExists) Directory.CreateDirectory(FastDirDbPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Resolve(FastDir, Configured, DataDirDbPath), Is.EqualTo(DataDirDbPath));
            Assert.That(_logger.LogList, Has.One.Contains("Keeping the existing database"));
        }
    }

    [Test]
    public void Db_dir_location_is_used_when_it_holds_a_database([Values] bool dataDirHoldsDatabase)
    {
        CreateDatabase(FastDirDbPath);
        if (dataDirHoldsDatabase) CreateDatabase(DataDirDbPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Resolve(FastDir, Configured, DataDirDbPath), Is.EqualTo(FastDirDbPath));
            Assert.That(_logger.LogList, Is.Empty);
        }
    }

    private string Resolve(string dbDir, string configured, string resolved) =>
        DatabaseDirectory.Resolve(dbDir, configured, resolved, new ILogger(_logger));

    private static void CreateDatabase(string path) => Directory.CreateDirectory(Path.Combine(path, "state"));
}
