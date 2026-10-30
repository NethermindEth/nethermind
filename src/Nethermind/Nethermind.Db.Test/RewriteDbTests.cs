// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
public class RewriteDbTests
{
    private const int EntriesPerColumn = 3_000;

    [TestCase(DbNames.Blocks)]
    [TestCase(DbNames.Receipts)]
    [TestCase(DbNames.BlobTransactions)]
    public async Task Rewrites_every_column_with_the_current_config(string dbName)
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper sharedCache = new();

        (IDisposable db, IDb[] columns) = Open(CreateFactory(new DbConfig(), sharedCache, basePath.Path), dbName);
        using (db)
        {
            foreach (IDb column in columns) Fill(column);
        }

        string fullPath = Path.Combine(basePath.Path, dbName);
        Directory.CreateDirectory(fullPath + "_rewrite");
        File.WriteAllText(Path.Combine(fullPath + "_rewrite", "CURRENT"), "left over from an interrupted rewrite");

        const string newOptions = "block_based_table_factory.block_size=7777;";
        DbConfig newConfig = new()
        {
            BlocksDbAdditionalRocksDbOptions = newOptions,
            ReceiptsDbAdditionalRocksDbOptions = newOptions,
            BlobTransactionsDbAdditionalRocksDbOptions = newOptions
        };
        await CreateStep(newConfig, sharedCache, basePath.Path, [dbName]).Execute(CancellationToken.None);
        // Read before reopening, which writes an OPTIONS file of its own.
        string options = File.ReadAllText(Directory.GetFiles(fullPath, "OPTIONS-*").Max()!);

        (db, columns) = Open(CreateFactory(newConfig, sharedCache, basePath.Path), dbName);
        using (db)
        using (Assert.EnterMultipleScope())
        {
            foreach (IDb column in columns)
            {
                Assert.That(column.GetAll(ordered: true).ToArray(), Is.EqualTo(Fill(new MemDb()).GetAll(ordered: true).ToArray()), column.Name);
            }

            Assert.That(options, Does.Contain("block_size=7777"));
            Assert.That(Directory.Exists(fullPath + "_rewrite"), Is.False);
            Assert.That(Directory.Exists(fullPath + "_old"), Is.False);
        }
    }

    [TestCase(new string[0], false)]
    [TestCase(new[] { DbNames.Blocks, DbNames.Receipts }, false)]
    [TestCase(new[] { DbNames.Headers }, false)]
    [TestCase(new[] { DbNames.Blocks }, true)]
    public async Task Rejects_requests_it_cannot_complete_safely(string[] arguments, bool interruptedSwap)
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper sharedCache = new();
        DbConfig dbConfig = new();

        using (IDb blocks = CreateFactory(dbConfig, sharedCache, basePath.Path).CreateDb(new DbSettings("Blocks", DbNames.Blocks)))
        {
            Fill(blocks);
        }

        if (interruptedSwap) Directory.CreateDirectory(Path.Combine(basePath.Path, DbNames.Blocks + "_old"));

        await Assert.ThatAsync(() => CreateStep(dbConfig, sharedCache, basePath.Path, arguments).Execute(CancellationToken.None),
            Throws.InstanceOf<InvalidConfigurationException>());
        Assert.That(File.Exists(Path.Combine(basePath.Path, DbNames.Blocks, "CURRENT")), Is.True);
    }

    private static RocksDbConfigFactory CreateConfigFactory(DbConfig dbConfig) =>
        new(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance);

    private static RocksDbFactory CreateFactory(DbConfig dbConfig, HyperClockCacheWrapper sharedCache, string basePath) =>
        new(CreateConfigFactory(dbConfig), dbConfig, sharedCache, LimboLogs.Instance, basePath);

    private static RewriteDb CreateStep(DbConfig dbConfig, HyperClockCacheWrapper sharedCache, string basePath, string[] arguments) =>
        new([new StepCommandSelection("rewrite-db", arguments)], new InitConfig { BaseDbPath = basePath }, dbConfig,
            CreateConfigFactory(dbConfig), sharedCache, LimboLogs.Instance);

    private static (IDisposable Db, IDb[] Columns) Open(IDbFactory factory, string dbName) => dbName switch
    {
        DbNames.Receipts => Columns(factory.CreateColumnsDb<ReceiptsColumns>(new DbSettings("Receipts", dbName))),
        DbNames.BlobTransactions => Columns(factory.CreateColumnsDb<BlobTxsColumns>(new DbSettings("BlobTransactions", dbName))),
        _ => Single(factory.CreateDb(new DbSettings("Blocks", dbName)))
    };

    private static (IDisposable, IDb[]) Single(IDb db) => (db, [db]);

    private static (IDisposable, IDb[]) Columns<T>(IColumnsDb<T> db) where T : struct, Enum =>
        (db, [.. db.ColumnKeys.Select(db.GetColumnDb)]);

    /// <remarks>Large enough values that each column spans several write chunks.</remarks>
    private static IDb Fill(IDb db)
    {
        byte[] key = new byte[4];
        for (int i = 0; i < EntriesPerColumn; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            byte[] value = new byte[1024];
            value.AsSpan().Fill((byte)i);
            db.PutSpan(key, value);
        }

        return db;
    }
}
