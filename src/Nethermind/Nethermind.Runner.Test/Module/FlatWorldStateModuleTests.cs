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
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;
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
            .AddModule(new FlatWorldStateModule(new FlatDbConfig { DropPruningTrieState = drop }))
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
        try
        {
            using IContainer container = new ContainerBuilder()
                .AddModule(new FlatWorldStateModule(flatDbConfig))
                .AddSingleton<IFlatDbConfig>(flatDbConfig)
                .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = baseDbPath })
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
}
