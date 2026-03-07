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

    [TestCase(1024, 819, TestName = "LowLimit")]
    [TestCase(100, 128, TestName = "MinimumEnforced")]
    [TestCase(9999, 7999, TestName = "Boundary")]
    public void WillCapMaxOpenFilesOnLowLimitSystem(int systemLimit, int expected)
    {
        DbConfig dbConfig = new DbConfig();
        RocksDbConfigFactory factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, systemLimit), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.MaxOpenFiles.Should().Be(expected);
    }

    [TestCase(65536, TestName = "HighLimit")]
    [TestCase(1048576, TestName = "TypicalLinuxSystemd")]
    [TestCase(10000, TestName = "ExactThreshold")]
    [TestCase(null, TestName = "UnknownLimit")]
    public void WillLeaveMaxOpenFilesUnlimited(int? systemLimit)
    {
        DbConfig dbConfig = new DbConfig();
        RocksDbConfigFactory factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, systemLimit), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.MaxOpenFiles.Should().BeNull();
    }

    [Test]
    public void WillNotOverrideUserConfiguredMaxOpenFiles()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.MaxOpenFiles = 3000;
        RocksDbConfigFactory factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 500), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        config.MaxOpenFiles.Should().Be(3000);
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
