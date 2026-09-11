// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Init.Modules;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatStateActivationPolicyTests
{
    [Test]
    public void FreshFlatDbStarts()
    {
        FlatStateActivationPolicy policy = CreatePolicy();

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
    }

    [Test]
    public void ExistingFlatDbIsPreservedWhenLegacyFilesRemain()
    {
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");
        FlatStateActivationPolicy policy = CreatePolicy(
            fileSystem: fileSystem,
            dbFactory: dbFactory,
            flatState: new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
        fileSystem.Directory.Received(0).EnumerateFiles(Arg.Any<string>(), "*", SearchOption.AllDirectories);
    }

    [Test]
    public void LegacyFilesInNestedStateDirectoryAreRejected()
    {
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(fileSystem: fileSystem, dbFactory: dbFactory))!;

        Assert.That(exception.Message, Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
        dbFactory.Received().GetFullDbPath(Arg.Is<DbSettings>(s => s.DbName == "State" && s.DbPath == DbNames.State));
    }

    [Test]
    public void WorldStateBoundaryResolutionRejectsLegacyStateBeforeBoundaryConstruction()
    {
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.ImportFromPruningTrieState.Returns(false);
        flatDbConfig.Layout.Returns(FlatLayout.Flat);
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns("Current");
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(StateId.PreGenesis);
        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);

        using IContainer container = new ContainerBuilder()
            .AddSingleton<IFlatDbConfig>(flatDbConfig)
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo(32L * 1024 * 1024 * 1024))
            .AddSingleton<IPersistence>(persistence)
            .AddSingleton<IDbFactory>(dbFactory)
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddModule(new WorldStateModule(initConfig))
            .Build();

        DependencyResolutionException exception = Assert.Throws<DependencyResolutionException>(() => container.Resolve<IStateBoundary>())!;
        Assert.That(exception.GetBaseException(), Is.TypeOf<InvalidConfigurationException>());
        Assert.That(exception.ToString(), Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
    }

    [TestCase("Hash")]
    [TestCase(" hash ")]
    [TestCase("HALFPATH")]
    [TestCase("0")]
    [TestCase("1")]
    public void ExplicitLegacyStateSchemaIsRejected(string schema)
    {
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns(schema);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(initConfig: initConfig))!;

        Assert.That(exception.Message, Does.Contain("Hash and HalfPath schemas"));
        Assert.That(exception.Message, Does.Contain("flatdbimportfrompruningtriestate"));
    }

    [TestCase("Enabled")]
    [TestCase("ImportFromPruningTrieState")]
    public void ExplicitLegacyFlatDbSettingIsRejected(string setting)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(setting != nameof(IFlatDbConfig.Enabled));
        flatDbConfig.ImportFromPruningTrieState.Returns(setting == nameof(IFlatDbConfig.ImportFromPruningTrieState));

        Assert.That(
            () => CreatePolicy(flatDbConfig: flatDbConfig),
            Throws.TypeOf<InvalidConfigurationException>()
                .With.Message.Contains(FlatStateActivationPolicy.LegacySchemaMessage));
    }

    [Test]
    public void FlatDbDisabledByTypedConfigurationIsRejected()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(false);

        Assert.That(
            () => CreatePolicy(flatDbConfig: flatDbConfig),
            Throws.TypeOf<InvalidConfigurationException>()
                .With.Message.Contains(FlatStateActivationPolicy.LegacySchemaMessage));
    }

    [TestCase(true, FlatLayout.Flat, 8, true)]
    [TestCase(true, FlatLayout.FlatInTrie, 8, false)]
    [TestCase(true, FlatLayout.Flat, 32, false)]
    public void AdvisesFlatInTrieLayoutOnLowMemory(bool enabled, FlatLayout layout, int availableMemoryGiB, bool expectWarn)
    {
        TestLogger testLogger = new();
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(enabled);
        flatDbConfig.Layout.Returns(layout);
        FlatStateActivationPolicy policy = CreatePolicy(
            flatDbConfig: flatDbConfig,
            availableMemoryBytes: availableMemoryGiB.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)));

        _ = policy.ShouldTurnOnFlatDb();
        bool warned = testLogger.LogList.Any(l => l.Contains("--FlatDb.Layout") && l.Contains(nameof(FlatLayout.FlatInTrie)));
        Assert.That(warned, Is.EqualTo(expectWarn));
    }

    private static FlatStateActivationPolicy CreatePolicy(
        IFlatDbConfig? flatDbConfig = null,
        IInitConfig? initConfig = null,
        IFileSystem? fileSystem = null,
        IDbFactory? dbFactory = null,
        StateId? flatState = null,
        long availableMemoryBytes = 32L * 1024 * 1024 * 1024,
        ILogManager? logManager = null)
    {
        if (flatDbConfig is null)
        {
            flatDbConfig = Substitute.For<IFlatDbConfig>();
            flatDbConfig.Enabled.Returns(true);
            flatDbConfig.ImportFromPruningTrieState.Returns(false);
            flatDbConfig.Layout.Returns(FlatLayout.Flat);
        }

        if (initConfig is null)
        {
            initConfig = Substitute.For<IInitConfig>();
            initConfig.StateDbKeyScheme.Returns("Current");
        }

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flatState ?? StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);

        if (fileSystem is null)
        {
            fileSystem = Substitute.For<IFileSystem>();
            IDirectory directory = Substitute.For<IDirectory>();
            fileSystem.Directory.Returns(directory);
            directory.Exists(Arg.Any<string>()).Returns(false);
        }

        if (dbFactory is null)
        {
            dbFactory = Substitute.For<IDbFactory>();
            dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns("state");
        }

        return new FlatStateActivationPolicy(
            flatDbConfig,
            initConfig,
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IPersistence>(() => flatPersistence),
            dbFactory,
            fileSystem,
            logManager ?? LimboLogs.Instance);
    }

    private static (IFileSystem FileSystem, IDbFactory DbFactory) CreateLegacyFileSystem(string marker)
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IDirectory directory = Substitute.For<IDirectory>();
        fileSystem.Directory.Returns(directory);
        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.EnumerateFiles(Arg.Any<string>(), "*", SearchOption.AllDirectories).Returns([marker]);

        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns("state");
        return (fileSystem, dbFactory);
    }
}
