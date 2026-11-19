// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

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
        await Fill(basePath.Path, new DbConfig(), dbName);

        string fullPath = Path.Combine(basePath.Path, dbName);
        Directory.CreateDirectory(fullPath + "_rewrite");
        File.WriteAllText(Path.Combine(fullPath + "_rewrite", "CURRENT"), "left over from an interrupted rewrite");
        // A marker belongs to the database and moves with it; any other file stays with the replaced directory.
        File.WriteAllText(Path.Combine(fullPath, "layout.marker"), "FlatInTrie");
        File.WriteAllText(Path.Combine(fullPath, "replaced.txt"), "");

        DbConfig newConfig = WithOptions("block_based_table_factory.block_size=7777;");
        await Execute(typeof(RewriteDb), basePath.Path, newConfig, [dbName]);
        // Read before reopening, which writes an OPTIONS file of its own.
        string options = File.ReadAllText(Directory.GetFiles(fullPath, "OPTIONS-*").Max()!);

        await AssertContents(basePath.Path, newConfig, dbName);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options, Does.Contain("block_size=7777"));
            Assert.That(File.ReadAllText(Path.Combine(fullPath, "layout.marker")), Is.EqualTo("FlatInTrie"));
            Assert.That(File.Exists(Path.Combine(fullPath, "replaced.txt")), Is.False);
            Assert.That(Directory.Exists(fullPath + "_rewrite"), Is.False);
            Assert.That(Directory.Exists(fullPath + "_old"), Is.False);
        }
    }

    [Test]
    public async Task Compacts_every_file_of_every_column(
        [Values(DbNames.Blocks, DbNames.Receipts, DbNames.BlobTransactions)] string dbName, [Values] bool blobFiles)
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        await Fill(basePath.Path, blobFiles ? WithOptions("enable_blob_files=true;min_blob_size=256;") : new DbConfig(), dbName);

        string fullPath = Path.Combine(basePath.Path, dbName);
        DbConfig dbConfig = new();
        await Execute(typeof(CompactDb), basePath.Path, dbConfig, [dbName]);
        string[] compactedOnce = Directory.GetFiles(fullPath, "*.sst");
        // Files already at the bottom level are rewritten only when the compaction forces it.
        await Execute(typeof(CompactDb), basePath.Path, dbConfig, [dbName]);

        Assert.That(Directory.GetFiles(fullPath, "*.sst").Intersect(compactedOnce), Is.Empty);
        await AssertContents(basePath.Path, dbConfig, dbName, static column =>
            Assert.That((LevelZero: ReadIntProperty(column, "rocksdb.num-files-at-level0"), BlobFiles: ReadIntProperty(column, "rocksdb.num-blob-files")),
                Is.EqualTo((0, 0)), column.Name));
    }

    [Test]
    public async Task Rejects_requests_it_cannot_complete_safely(
        [ValueSource(nameof(RejectedRequests))] RejectedRequest request, [Values(typeof(RewriteDb), typeof(CompactDb))] Type step)
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        await Fill(basePath.Path, new DbConfig(), DbNames.Blocks);

        string? planted = request.Planted is null ? null : Path.Combine(basePath.Path, request.Planted);
        if (planted is not null)
        {
            if (Path.EndsInDirectorySeparator(planted)) Directory.CreateDirectory(planted);
            else File.WriteAllText(planted, "");
        }

        if (request.Link is not null)
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(basePath.Path, request.Link), Path.Combine(basePath.Path, DbNames.Blocks));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Assert.Ignore($"Cannot create a directory link here: {e.Message}");
            }
        }

        await Assert.ThatAsync(() => Execute(step, basePath.Path, new DbConfig(), request.Arguments),
            Throws.InstanceOf<InvalidConfigurationException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(basePath.Path, DbNames.Blocks, "CURRENT")), Is.True);
            if (planted is not null) Assert.That(Path.Exists(planted), Is.True);
        }
    }

    [Test]
    public async Task Names_the_complete_copy_when_interrupted_between_the_moves()
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        await Fill(basePath.Path, new DbConfig(), DbNames.Blocks);

        string fullPath = Path.Combine(basePath.Path, DbNames.Blocks);
        Directory.Move(fullPath, fullPath + "_old");
        Directory.CreateDirectory(fullPath + "_rewrite");

        await Assert.ThatAsync(() => Execute(typeof(RewriteDb), basePath.Path, new DbConfig(), [DbNames.Blocks]),
            Throws.InstanceOf<InvalidConfigurationException>().With.Message.Contains(fullPath + "_rewrite"));
    }

    [Test]
    public async Task Cancelled_run_leaves_the_database_as_it_was([Values(typeof(RewriteDb), typeof(CompactDb))] Type step)
    {
        using TempPath basePath = TempPath.GetTempDirectory();
        await Fill(basePath.Path, new DbConfig(), DbNames.Receipts);

        await Assert.ThatAsync(() => Execute(step, basePath.Path, new DbConfig(), [DbNames.Receipts], new CancellationToken(canceled: true)),
            Throws.InstanceOf<OperationCanceledException>());

        await AssertContents(basePath.Path, new DbConfig(), DbNames.Receipts);
        Assert.That(Directory.Exists(Path.Combine(basePath.Path, DbNames.Receipts + "_rewrite")), Is.False);
    }

    public sealed record RejectedRequest(string Name, string[] Arguments, string? Planted = null, string? Link = null)
    {
        public override string ToString() => Name;
    }

    private static IEnumerable<RejectedRequest> RejectedRequests()
    {
        yield return new("no database", []);
        yield return new("two databases", [DbNames.Blocks, DbNames.Receipts]);
        yield return new("missing database", [DbNames.Headers]);
        yield return new("path outside the base path", ["../" + DbNames.Blocks]);
        yield return new("trailing separator", [DbNames.Blocks + "/"]);
        yield return new("log index", [DbNames.LogIndex]);
        yield return new("interrupted swap", [DbNames.Blocks], DbNames.Blocks + "_old" + Path.DirectorySeparatorChar);
        yield return new("corrupt marker", [DbNames.Blocks], Path.Combine(DbNames.Blocks, "corrupt.marker"));
        yield return new("repaired marker", [DbNames.Blocks], Path.Combine(DbNames.Blocks, "repaired.marker"));
        yield return new("linked database", ["linked"], Link: "linked");
    }

    private static DbConfig WithOptions(string options) => new()
    {
        BlocksDbAdditionalRocksDbOptions = options,
        ReceiptsDbAdditionalRocksDbOptions = options,
        BlobTransactionsDbAdditionalRocksDbOptions = options
    };

    private static IContainer CreateContainer(string basePath, DbConfig dbConfig, StepCommandSelection? selection = null)
    {
        InitConfig initConfig = new() { BaseDbPath = basePath };
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new DbModule(initConfig, new ReceiptConfig(), new SyncConfig()))
            .AddSingleton<IDbConfig>(dbConfig)
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo())
            .AddSingleton<IFileSystem, RealFileSystem>()
            .AddSingleton<ILogManager>(LimboLogs.Instance);
        if (selection is not null) builder.AddSingleton(selection);
        return builder.Build();
    }

    private static async Task Execute(Type step, string basePath, DbConfig dbConfig, string[] arguments,
        CancellationToken cancellationToken = default)
    {
        string command = step.GetCustomAttribute<StepCommandAttribute>()!.Name;
        await using IContainer container = CreateContainer(basePath, dbConfig, new StepCommandSelection(command, arguments));
        await ((IStep)container.Resolve(step)).Execute(cancellationToken);
    }

    /// <summary>Writes different entries to each column and flushes them to level 0.</summary>
    private static async Task Fill(string basePath, DbConfig dbConfig, string dbName)
    {
        await using IContainer container = CreateContainer(basePath, dbConfig);
        (IDisposable db, IDb[] columns) = Open(container.Resolve<IDbFactory>(), dbName);
        using (db)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                Fill(columns[i], i).Flush();
                Assert.That(ReadIntProperty(columns[i], "rocksdb.num-files-at-level0"), Is.Positive, columns[i].Name);
            }
        }
    }

    private static async Task AssertContents(string basePath, DbConfig dbConfig, string dbName, Action<IDb>? assertColumn = null)
    {
        await using IContainer container = CreateContainer(basePath, dbConfig);
        (IDisposable db, IDb[] columns) = Open(container.Resolve<IDbFactory>(), dbName);
        using (db)
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < columns.Length; i++)
            {
                Assert.That(columns[i].GetAll(ordered: true).ToArray(), Is.EqualTo(Fill(new MemDb(), i).GetAll(ordered: true).ToArray()), columns[i].Name);
                assertColumn?.Invoke(columns[i]);
            }
        }
    }

    private static (IDisposable Db, IDb[] Columns) Open(IDbFactory factory, string dbName) => dbName switch
    {
        DbNames.Receipts => Columns(factory.CreateColumnsDb<ReceiptsColumns>(new DbSettings("Receipts", dbName))),
        DbNames.BlobTransactions => Columns(factory.CreateColumnsDb<BlobTxsColumns>(new DbSettings("BlobTransactions", dbName))),
        _ => Single(factory.CreateDb(new DbSettings("Blocks", dbName)))
    };

    private static (IDisposable, IDb[]) Single(IDb db) => (db, [db]);

    private static (IDisposable, IDb[]) Columns<T>(IColumnsDb<T> db) where T : struct, Enum =>
        (db, [.. db.ColumnKeys.Select(db.GetColumnDb)]);

    private static int ReadIntProperty(IDb column, string name) =>
        int.Parse(((DbOnTheRocks)column).GatherProperty(name)!);

    /// <remarks>Large enough values that each column spans several write chunks.</remarks>
    private static IDb Fill(IDb db, int seed)
    {
        byte[] key = new byte[4];
        for (int i = 0; i < EntriesPerColumn; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(key, i);
            byte[] value = new byte[1024];
            value.AsSpan().Fill((byte)(i + seed));
            db.PutSpan(key, value);
        }

        return db;
    }
}
