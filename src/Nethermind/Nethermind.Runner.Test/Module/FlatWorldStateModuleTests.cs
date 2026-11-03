// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Testably.Abstractions;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatWorldStateModuleTests
{
    [Test]
    public void Drop_step_is_registered_before_InitializeBlockchain_only_when_flagged([Values] bool drop)
    {
        // The dependent edge is what keeps the deletion inside init rather than under block processing.
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(new FlatDbConfig { DropPruningTrieState = drop }, new InitConfig()))
            .Build();

        StepInfo step = container.Resolve<IEnumerable<StepInfo>>().SingleOrDefault(s => s.StepBaseType == typeof(DropPruningTrieState));

        Assert.That(step is not null, Is.EqualTo(drop));
        if (drop)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(step.Dependencies, Does.Contain(typeof(InitializeBlockTree)));
                Assert.That(step.Dependents, Does.Contain(typeof(InitializeBlockchain)));
            }
        }
    }

    [TestCase("")]
    [TestCase("./")]
    public void File_paths_are_rooted_like_the_databases_under_a_relative_BaseDbPath(string prefix)
    {
        string name = $"flat-paths-{Guid.NewGuid():N}";
        string baseDbPath = prefix + name;
        string dbRootedBaseDbPath = prefix.Length == 0 ? Path.Combine(PathUtils.ExecutingDirectory, name) : baseDbPath;
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(new RealFileSystem().Path);
        FlatDbConfig flatDbConfig = new();
        InitConfig initConfig = new() { BaseDbPath = baseDbPath };
        try
        {
            using IContainer container = new ContainerBuilder()
                .AddModule(new FlatWorldStateModule(flatDbConfig, initConfig))
                .AddSingleton<IFlatDbConfig>(flatDbConfig)
                .AddSingleton<IInitConfig>(initConfig)
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IColumnsDb<FlatDbColumns>>(new MemColumnsDb<FlatDbColumns>())
                .AddSingleton(fileSystem)
                .Build();

            container.Resolve<ITrieNodeLog>();
            container.Resolve<IArenaManager>();
            container.Resolve<BlobArenaManager>();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.Exists(Path.Combine(dbRootedBaseDbPath, "persistedSnapshot", "arena")), "arena");
                Assert.That(Directory.Exists(Path.Combine(dbRootedBaseDbPath, "persistedSnapshot", "blob")), "blob");
            }
            fileSystem.Directory.Received().Exists(Path.Combine(dbRootedBaseDbPath, "flatTrieNodeLog"));
        }
        finally
        {
            if (Directory.Exists(dbRootedBaseDbPath)) Directory.Delete(dbRootedBaseDbPath, recursive: true);
        }
    }

    /// <summary>The smallest file Paprika opens: its root pages and one more.</summary>
    private const long MinFileSize = (PaprikaFlatStore.HistoryDepth + 1L) * 4096;

    // Each case is a valid PaprikaFlat config but for one setting, which the refusal names; null for a valid config.
    [TestCase(false, MinFileSize, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.TrieNodeLogEnabled))]
    [TestCase(true, 0L, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes))]
    [TestCase(true, -4096L, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes))]
    [TestCase(true, MinFileSize - 4096, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes))]
    [TestCase(true, MinFileSize + 1, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes))]
    [TestCase(true, (1L << 40) + 4096, 0L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes))]
    [TestCase(true, MinFileSize, -1L, 2, DiagnosticMode.None, nameof(IFlatDbConfig.PaprikaFlatPageCacheBytes))]
    [TestCase(true, MinFileSize, 0L, 16, DiagnosticMode.None, nameof(IFlatDbConfig.TrieNodeLogStateShardCount))]
    [TestCase(true, MinFileSize, 0L, 2, DiagnosticMode.ReadOnlyDb, nameof(DiagnosticMode.ReadOnlyDb))]
    [TestCase(true, MinFileSize, 0L, 2, DiagnosticMode.RpcDb, nameof(DiagnosticMode.RpcDb))]
    [TestCase(true, MinFileSize, 0L, 8, DiagnosticMode.None, null)]
    [TestCase(true, 1L << 40, 0L, 2, DiagnosticMode.MemDb, null)]
    public void PaprikaFlat_config_is_validated_when_the_module_loads(bool trieNodeLog, long fileSize, long pageCache, int shardsPerPartition, DiagnosticMode mode, string refusal)
    {
        FlatDbConfig flatDbConfig = new()
        {
            Layout = FlatLayout.PaprikaFlat,
            TrieNodeLogEnabled = trieNodeLog,
            PaprikaFlatFileSizeBytes = fileSize,
            PaprikaFlatPageCacheBytes = pageCache,
            TrieNodeLogStateShardCount = shardsPerPartition,
            TrieNodeLogStorageShardCount = shardsPerPartition,
        };

        IResolveConstraint outcome = refusal is null
            ? Throws.Nothing
            : Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(refusal).And.Property(nameof(InvalidConfigurationException.ExitCode)).EqualTo(-1);

        Assert.That(() => new ContainerBuilder().AddModule(new FlatWorldStateModule(flatDbConfig, new InitConfig { DiagnosticMode = mode })).Build().Dispose(), outcome);
    }

    [Test]
    public void Metadata_store_follows_the_layout([Values] FlatLayout layout)
    {
        FlatDbConfig flatDbConfig = new() { Layout = layout, TrieNodeLogEnabled = true };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(flatDbConfig, new InitConfig()))
            .AddSingleton<IFlatDbConfig>(flatDbConfig)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(new SnapshotableMemColumnsDb<FlatDbColumns>())
            .Build();

        Assert.That(container.Resolve<IFlatMetadataStore>(), layout == FlatLayout.PaprikaFlat ? Is.TypeOf<PaprikaFlatStore>() : Is.TypeOf<ColumnsFlatMetadataStore>());
    }

    [Test]
    public void PaprikaFlat_file_is_rooted_like_the_databases_unless_in_memory([Values("", "./")] string prefix, [Values] bool memDb)
    {
        string name = $"flat-paprika-{Guid.NewGuid():N}";
        string dbRootedBaseDbPath = prefix.Length == 0 ? Path.Combine(PathUtils.ExecutingDirectory, name) : prefix + name;
        try
        {
            // In memory, the 1 TiB default, which only the 4 GiB cap on the in-memory store lets Linux reserve.
            long fileSize = memDb ? new FlatDbConfig().PaprikaFlatFileSizeBytes : 1024 * 1024;
            StartPaprikaFlat(prefix + name, memDb ? DiagnosticMode.MemDb : DiagnosticMode.None, fileSize, new SnapshotableMemColumnsDb<FlatDbColumns>());

            Assert.That(Directory.Exists(Path.Combine(dbRootedBaseDbPath, "flatPaprika")), Is.EqualTo(!memDb));
            if (!memDb) Assert.That(File.Exists(Path.Combine(dbRootedBaseDbPath, "flatPaprika", "paprika.db")));
        }
        finally
        {
            if (Directory.Exists(dbRootedBaseDbPath)) Directory.Delete(dbRootedBaseDbPath, recursive: true);
        }
    }

    [Test]
    public void PaprikaFlat_file_can_grow_but_not_shrink()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        StartPaprikaFlat(directory.Path, DiagnosticMode.None, MinFileSize, db);
        StartPaprikaFlat(directory.Path, DiagnosticMode.None, 1024 * 1024, db);

        Exception refusal = Assert.Catch(() => StartPaprikaFlat(directory.Path, DiagnosticMode.None, MinFileSize, db))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FileInfo(Path.Combine(directory.Path, "flatPaprika", "paprika.db")).Length, Is.EqualTo(1024 * 1024));
            Assert.That(refusal.GetBaseException(), Is.TypeOf<InvalidConfigurationException>().With.Message.Contains("can only grow"));
        }
    }

    /// <summary>Starts and stops a PaprikaFlat node's flat persistence over <paramref name="db"/>, which stands in for RocksDB.</summary>
    private static void StartPaprikaFlat(string baseDbPath, DiagnosticMode mode, long fileSize, IColumnsDb<FlatDbColumns> db)
    {
        FlatDbConfig flatDbConfig = new() { Layout = FlatLayout.PaprikaFlat, TrieNodeLogEnabled = true, PaprikaFlatFileSizeBytes = fileSize };
        InitConfig initConfig = new() { BaseDbPath = baseDbPath, DiagnosticMode = mode };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(flatDbConfig, initConfig))
            .AddSingleton<IFlatDbConfig>(flatDbConfig)
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(db)
            .AddSingleton<IFileSystem>(new RealFileSystem())
            .Build();
        container.Resolve<PaprikaFlatPersistence>();
    }
}
