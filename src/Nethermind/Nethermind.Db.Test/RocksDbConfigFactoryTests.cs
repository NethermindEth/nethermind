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

    [Test]
    public void WillAutomaticallySetMaxOpenFilesWhenLowSystemLimit()
    {
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 1024), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // With low system limit of 1024: max(128, 1024 * 0.8 / 15) = max(128, 54) = 128
        config.MaxOpenFiles.Should().Be(128);
    }

    [Test]
    public void WillSetCorrectLimitForHighSystemLimit()
    {
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 65536), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // With system limit of 65536: max(128, 65536 * 0.8 / 15) = max(128, 3495) = 3495
        config.MaxOpenFiles.Should().Be(3495);
    }

    [Test]
    public void WillNotOverrideUserConfiguredMaxOpenFiles()
    {
        var dbConfig = new DbConfig();
        dbConfig.MaxOpenFiles = 3000;
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 10000), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // Should keep user-configured value
        config.MaxOpenFiles.Should().Be(3000);
    }

    [Test]
    public void WillNotSetMaxOpenFilesWhenSystemLimitUnknown()
    {
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, null), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // Should be null when system limit is unknown
        config.MaxOpenFiles.Should().BeNull();
    }

    [Test]
    public void WillEnforceMinimumMaxOpenFiles()
    {
        var dbConfig = new DbConfig();
        // Very low system limit should still give minimum of 128
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 100), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // With system limit of 100: max(128, 100 * 0.8 / 15) = max(128, 5) = 128
        config.MaxOpenFiles.Should().Be(128);
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
