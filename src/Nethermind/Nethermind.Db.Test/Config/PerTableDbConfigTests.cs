// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        string[] tables = new[]
        {
            DbNames.Storage,
            DbNames.State,
            DbNames.Code,
            DbNames.Blocks,
            DbNames.Headers,
            DbNames.Receipts,
            DbNames.BlockInfos,
            DbNames.Bloom,
            DbNames.Witness,
            DbNames.CHT,
            DbNames.Metadata,
        };

        foreach (string table in tables)
        {
            PerTableDbConfig config = new PerTableDbConfig(dbConfig, new RocksDbSettings(table, ""));

            object _ = config.CacheIndexAndFilterBlocks;
            _ = config.BlockCacheSize;
            _ = config.WriteBufferSize;
            _ = config.WriteBufferNumber;
            _ = config.MaxOpenFiles;
        }
    }

    [Test]
    public void When_PerTableConfigIsAvailable_UsePerTableConfig()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.MaxOpenFiles = 2;
        dbConfig.ReceiptsDbMaxOpenFiles = 3;

        PerTableDbConfig config = new PerTableDbConfig(dbConfig, new RocksDbSettings(DbNames.Receipts, ""));
        config.MaxOpenFiles.Should().Be(3);
    }

    [Test]
    public void When_PerTableConfigIsNotAvailable_UseGeneralConfig()
    {
        DbConfig dbConfig = new DbConfig();
        dbConfig.MaxOpenFiles = 2;

        PerTableDbConfig config = new PerTableDbConfig(dbConfig, new RocksDbSettings(DbNames.Receipts, ""));
        config.MaxOpenFiles.Should().Be(2);
    }
}
