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
}
