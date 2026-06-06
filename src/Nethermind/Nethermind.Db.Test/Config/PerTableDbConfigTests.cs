// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Db.Rocks.Config;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Db.Test.Config;

public class PerTableDbConfigTests
{
    [Test]
    public void CanReadAllConfigForAllTable()
    {
        DbConfig dbConfig = new();
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
            PerTableDbConfig config = new(dbConfig, table, validate: false);

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
        DbConfig dbConfig = new();
        dbConfig.RocksDbOptions = "some_option=1;";
        dbConfig.ReceiptsDbRocksDbOptions = "some_option=2;";
        dbConfig.ReceiptsBlocksDbRocksDbOptions = "some_option=3;";

        PerTableDbConfig config = new(dbConfig, DbNames.Receipts, "Blocks");
        Assert.That(config.RocksDbOptions, Is.EqualTo("some_option=1;some_option=2;some_option=3;"));
    }

    [Test]
    public void When_PerTableConfigIsAvailable_UsePerTableConfig()
    {
        DbConfig dbConfig = new();
        dbConfig.RocksDbOptions = "some_option=1;";
        dbConfig.ReceiptsDbRocksDbOptions = "some_option=2;";
        dbConfig.ReceiptsBlocksDbRocksDbOptions = "some_option=3;";

        PerTableDbConfig config = new(dbConfig, DbNames.Receipts);
        Assert.That(config.RocksDbOptions, Is.EqualTo("some_option=1;some_option=2;"));
    }

    [Test]
    public void When_PerTableConfigIsNotAvailable_UseGeneralConfig()
    {
        DbConfig dbConfig = new();
        dbConfig.MaxOpenFiles = 2;

        PerTableDbConfig config = new(dbConfig, DbNames.Receipts);
        Assert.That(config.MaxOpenFiles, Is.EqualTo(2));
    }

    [Test]
    public void FlatStorageNodesDefault_UsesCommonFlatOptionsOnly()
    {
        DbConfig dbConfig = new();

        PerTableDbConfig config = new(dbConfig, nameof(DbNames.Flat), nameof(FlatDbColumns.StorageNodes), validate: false);

        Assert.That(config.RocksDbOptions, Does.Contain("block_based_table_factory.block_restart_interval=4;"));
        Assert.That(config.RocksDbOptions, Does.Not.Contain("block_based_table_factory.block_restart_interval=8;"));
        Assert.That(config.RocksDbOptions, Does.Not.Contain("max_bytes_for_level_base=350000000;"));
        Assert.That(config.RocksDbOptions, Does.Not.Contain("write_buffer_size=64000000;"));
        Assert.That(config.RocksDbOptions, Does.Not.Contain("max_write_buffer_number=8;"));
    }

    [Test]
    public void AllDbConfigMemberMustBeDeclaredInIDbConfig()
    {
        Type dbConfigType = typeof(DbConfig);
        Type iDbConfigType = typeof(IDbConfig);

        foreach (PropertyInfo propertyInfo in dbConfigType.GetProperties())
        {
            Assert.That(iDbConfigType.GetProperty(propertyInfo.Name), Is.Not.Null, $"{propertyInfo.Name} is missing in {nameof(IDbConfig)}");
        }
    }
}
