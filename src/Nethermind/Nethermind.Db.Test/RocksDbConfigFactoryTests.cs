// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
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
        Console.Error.WriteLine(config.RocksDbOptions);
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
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(100.GiB()), LimboLogs.Instance);
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
        config.WriteBufferSize.Should().Be((ulong)500.MB());
    }

    [Test]
    public void WillAutomaticallySetMaxOpenFilesWhenNotConfigured()
    {
        var dbConfig = new DbConfig();
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 10000), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // With system limit of 10000, should set per-db limit to 10000/20 = 500
        config.MaxOpenFiles.Should().Be(500);
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
        // Very low system limit (e.g., 1000) should still give minimum of 256
        var factory = new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(0, 1000), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        // With system limit of 1000, would be 1000/20 = 50, but minimum is 256
        config.MaxOpenFiles.Should().Be(256);
    }
}
