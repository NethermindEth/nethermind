// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class RocksDbConfigFactoryTests
{
    [Test]
    public void CanFetchNormally()
    {
        DbConfig dbConfig = new();
        RocksDbConfigFactory factory = new(dbConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("Metadata", null);
        Assert.That(config.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + dbConfig.MetadataDbRocksDbOptions));
    }

    [TestCase(1024, ExpectedResult = 819, TestName = "Caps to 80% on low limit")]
    [TestCase(100, ExpectedResult = 128, TestName = "Enforces minimum of 128 on very low limit")]
    [TestCase(9999, ExpectedResult = 7999, TestName = "Caps just below threshold")]
    [TestCase(65536, ExpectedResult = null, TestName = "Unlimited on high limit")]
    [TestCase(1048576, ExpectedResult = null, TestName = "Unlimited on typical Linux systemd limit")]
    [TestCase(10000, ExpectedResult = null, TestName = "Unlimited at exact threshold")]
    [TestCase(null, ExpectedResult = null, TestName = "Unlimited when system limit unknown")]
    [TestCase(500, 3000, ExpectedResult = 3000, TestName = "Preserves user-configured value")]
    public int? MaxOpenFilesIsSetCorrectly(int? systemLimit, int? userConfigured = null)
    {
        DbConfig dbConfig = new() { MaxOpenFiles = userConfigured };
        RocksDbConfigFactory factory = new(dbConfig, new TestHardwareInfo(0, systemLimit), LimboLogs.Instance);
        return factory.GetForDatabase("Metadata", null).MaxOpenFiles;
    }

    [Test]
    public void WillApplySkipSstFileSizeChecksWhenConfigExplicitlyEnabled()
    {
        DbConfig dbConfig = new();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = true;
        RocksDbConfigFactory factory = new(dbConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("Metadata", null);
        Assert.That(config.RocksDbOptions, Does.Contain("skip_checking_sst_file_sizes_on_db_open=true;"));
    }

    [Test]
    public void WillNotApplySkipSstFileSizeChecksWhenConfigExplicitlyDisabled()
    {
        DbConfig dbConfig = new();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = false;
        RocksDbConfigFactory factory = new(dbConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("Metadata", null);
        Assert.That(config.RocksDbOptions, Does.Not.Contain("skip_checking_sst_file_sizes_on_db_open"));
    }
}
