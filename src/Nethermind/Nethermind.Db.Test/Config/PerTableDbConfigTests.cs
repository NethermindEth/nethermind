// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

using FluentAssertions;

using Nethermind.Db.Rocks.Config;

using NUnit.Framework;

namespace Nethermind.Db.Test.Config;

public class PerTableDbConfigTests
{
    [Test]
    public void CanReadAllConfigForAllTable()
    {
        DbConfig dbConfig = new DbConfig();
        string[] tables =
        [
            DbNames.Storage,
            DbNames.State,
            DbNames.Code,
            DbNames.Blocks,
            DbNames.Headers,
            DbNames.Receipts,
            DbNames.BlockInfos,
            DbNames.Bloom,
            DbNames.Metadata
        ];

        foreach (string table in tables)
        {
            PerTableDbConfig config = new PerTableDbConfig(dbConfig, new DbSettings(table, ""));

            object _ = config.RocksDbOptions;
            _ = config.AdditionalRocksDbOptions;
            _ = config.WriteBufferSize;
            _ = config.WriteBufferNumber;
            _ = config.MaxOpenFiles;
        }
    }

    [Test]
    public void When_ColumnDb_UsePerTableConfig()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.RocksDbOptions = "some_option=1;";
        dbConfig.ReceiptsDbRocksDbOptions = "some_option=2;";
        dbConfig.ReceiptsBlocksDbRocksDbOptions = "some_option=3;";

        PerTableDbConfig config = new PerTableDbConfig(dbConfig, new DbSettings(DbNames.Receipts, ""), "Blocks");
        config.RocksDbOptions.Should().Be("some_option=1;some_option=2;some_option=3;");
    }

    [Test]
    public void When_PerTableConfigIsAvailable_UsePerTableConfig()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.RocksDbOptions = "some_option=1;";
        dbConfig.ReceiptsDbRocksDbOptions = "some_option=2;";
        dbConfig.ReceiptsBlocksDbRocksDbOptions = "some_option=3;";

        PerTableDbConfig config = new PerTableDbConfig(dbConfig, new DbSettings(DbNames.Receipts, ""));
        config.RocksDbOptions.Should().Be("some_option=1;some_option=2;");
    }

    [Test]
    public void When_PerTableConfigIsNotAvailable_UseGeneralConfig()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.MaxOpenFiles = 2;

        PerTableDbConfig config = new PerTableDbConfig(dbConfig, new DbSettings(DbNames.Receipts, ""));
        config.MaxOpenFiles.Should().Be(2);
    }

    [Test]
    public void AllDbConfigMemberMustBeDeclaredInIDbConfig()
    {
        Type dbConfigType = typeof(DbConfig);
        Type iDbConfigType = typeof(IDbConfig);

        foreach (PropertyInfo propertyInfo in dbConfigType.Properties())
        {
            iDbConfigType.GetProperty(propertyInfo.Name).Should().NotBeNull($"{propertyInfo.Name} is missing in {nameof(IDbConfig)}");
        }
    }
}
