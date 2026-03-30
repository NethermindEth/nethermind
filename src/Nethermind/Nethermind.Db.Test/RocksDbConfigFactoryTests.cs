// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
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
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.RocksDbOptions.Should().Be(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions);
    }

    [Test]
    public void WillOverrideStateConfigOnArchiveMode()
    {
        var dbConfig = new DbConfig();
        var pruningConfig = new PruningConfig();
        pruningConfig.Mode = PruningMode.Full;
        var factory = new RocksDbConfigFactory(dbConfig, pruningConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.RocksDbOptions.Should().Be(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions + dbConfig.StateDbArchiveModeRocksDbOptions);
    }

    [Test]
    public void WillOverrideStateConfigWhenMemoryIsHigh()
    {
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(100.GiB), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.RocksDbOptions.Should().Be(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions + dbConfig.StateDbLargeMemoryRocksDbOptions);
    }

    [Test]
    public void WillOverrideStateConfigWhenDirtyCachesTooHigh()
    {
        var dbConfig = new DbConfig();
        var pruningConfig = new PruningConfig();
        pruningConfig.CacheMb = 20000;
        pruningConfig.DirtyCacheMb = 10000;
        var factory = new RocksDbConfigFactory(dbConfig, pruningConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.WriteBufferSize.Should().Be((ulong)500.MB);
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
        RocksDbConfigFactory factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, systemLimit), LimboLogs.Instance);
        return factory.GetForDatabase("State0", null).MaxOpenFiles;
    }

    [Test]
    public void WillApplySkipSstFileSizeChecksWhenConfigExplicitlyEnabled()
    {
        var dbConfig = new DbConfig();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = true;
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.RocksDbOptions.Should().Contain("skip_checking_sst_file_sizes_on_db_open=true;");
    }

    [Test]
    public void WillNotApplySkipSstFileSizeChecksWhenConfigExplicitlyDisabled()
    {
        var dbConfig = new DbConfig();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = false;
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.RocksDbOptions.Should().NotContain("skip_checking_sst_file_sizes_on_db_open");
    }
}
