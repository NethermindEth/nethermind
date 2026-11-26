// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public void Global_defaults_ignore_table_and_column_overrides([Values(null, "Blocks")] string? columnName)
    {
        DbConfig dbConfig = new()
        {
            RocksDbOptions = "write_buffer_size=100;",
            AdditionalRocksDbOptions = "max_write_buffer_number=2;",
            StateDbRocksDbOptions = "state_only=true;",
            ReceiptsBlocksDbRocksDbOptions = "column_only=true;",
            WriteAheadLogSync = true,
            ReadAheadSize = 123,
            VerifyChecksum = false,
            EnableFileWarmer = true,
            CompressibilityHint = 0.7,
            FlushOnExit = FlushOnExitMode.Full,
            EnableDbStatistics = true,
            StatsDumpPeriodSec = 42,
            SkipCheckingSstFileSizesOnDbOpen = true
        };
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig { Mode = PruningMode.Full }, new TestHardwareInfo(100.GiB, 1024), LimboLogs.Instance);

        IRocksDbConfig config = factory.GetForDatabase(null, columnName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.RocksDbOptions, Is.EqualTo("write_buffer_size=100;skip_checking_sst_file_sizes_on_db_open=true;"));
            Assert.That(config.AdditionalRocksDbOptions, Is.EqualTo(dbConfig.AdditionalRocksDbOptions));
            Assert.That(config.WriteBufferSize, Is.Null);
            Assert.That(config.WriteBufferNumber, Is.Null);
            Assert.That(config.RowCacheSize, Is.Null);
            Assert.That(config.MaxOpenFiles, Is.EqualTo(819));
            Assert.That(config.WriteAheadLogSync, Is.True);
            Assert.That(config.ReadAheadSize, Is.EqualTo(123));
            Assert.That(config.VerifyChecksum, Is.False);
            Assert.That(config.EnableFileWarmer, Is.True);
            Assert.That(config.CompressibilityHint, Is.EqualTo(0.7));
            Assert.That(config.FlushOnExit, Is.EqualTo(FlushOnExitMode.Full));
            Assert.That(config.EnableDbStatistics, Is.True);
            Assert.That(config.StatsDumpPeriodSec, Is.EqualTo(42));
        }
    }

    [Test]
    public void CanFetchNormally()
    {
        DbConfig dbConfig = new();
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions));
    }

    [Test]
    public void WillOverrideStateConfigOnArchiveMode()
    {
        DbConfig dbConfig = new();
        PruningConfig pruningConfig = new();
        pruningConfig.Mode = PruningMode.Full;
        RocksDbConfigFactory factory = new(dbConfig, pruningConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions + dbConfig.StateDbArchiveModeRocksDbOptions));
    }

    [Test]
    public void WillOverrideStateConfigWhenMemoryIsHigh()
    {
        DbConfig dbConfig = new();
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(100.GiB), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions + dbConfig.StateDbRocksDbOptions + dbConfig.StateDbLargeMemoryRocksDbOptions));
    }

    [Test]
    public void WillOverrideStateConfigWhenDirtyCachesTooHigh()
    {
        DbConfig dbConfig = new();
        PruningConfig pruningConfig = new();
        pruningConfig.CacheMb = 20000;
        pruningConfig.DirtyCacheMb = 10000;
        RocksDbConfigFactory factory = new(dbConfig, pruningConfig, new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.WriteBufferSize, Is.EqualTo(500UL.MB));
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
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0, systemLimit), LimboLogs.Instance);
        return factory.GetForDatabase("State0", null).MaxOpenFiles;
    }

    [Test]
    public void WillApplySkipSstFileSizeChecksWhenConfigExplicitlyEnabled()
    {
        DbConfig dbConfig = new();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = true;
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.RocksDbOptions, Does.Contain("skip_checking_sst_file_sizes_on_db_open=true;"));
    }

    [Test]
    public void WillNotApplySkipSstFileSizeChecksWhenConfigExplicitlyDisabled()
    {
        DbConfig dbConfig = new();
        dbConfig.SkipCheckingSstFileSizesOnDbOpen = false;
        RocksDbConfigFactory factory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        IRocksDbConfig config = factory.GetForDatabase("State0", null);
        Assert.That(config.RocksDbOptions, Does.Not.Contain("skip_checking_sst_file_sizes_on_db_open"));
    }
}
