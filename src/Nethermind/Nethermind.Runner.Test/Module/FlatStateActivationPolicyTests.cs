// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
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
    public void FreshFlatDbStarts() => Assert.DoesNotThrow(() => CreatePolicy());

    [Test]
    public void ExistingFlatDbIsPreservedWhenLegacyFilesRemain()
    {
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");
        CreatePolicy(
            fileSystem: fileSystem,
            dbFactory: dbFactory,
            flatState: new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));

        fileSystem.Directory.Received(0).EnumerateFiles(Arg.Any<string>(), "*", SearchOption.AllDirectories);
    }

    [Test]
    public void LegacyFilesInNestedStateDirectoryAreRejected()
    {
        const string statePath = "C:\\data\\nethermind\\state";
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001", statePath);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(fileSystem: fileSystem, dbFactory: dbFactory))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.StartWith("Legacy state database files detected at 'C:\\data\\nethermind\\state'."));
            Assert.That(exception.Message, Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
            dbFactory.Received().GetFullDbPath(Arg.Is<DbSettings>(s => s.DbName == "State" && s.DbPath == DbNames.State));
        }
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

        using IContainer container = CreateProductionContainerBuilder(flatDbConfig, initConfig, fileSystem, dbFactory).Build();

        DependencyResolutionException exception = Assert.Throws<DependencyResolutionException>(() => container.Resolve<IStateBoundary>())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.GetBaseException(), Is.TypeOf<InvalidConfigurationException>());
            Assert.That(exception.ToString(), Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
        }
    }

    [Test]
    public void FlatStateValidationStepRejectsUnsupportedConfigurationWhenStateBoundaryIsOverridden()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(false);
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns("Current");

        ContainerBuilder builder = CreateProductionContainerBuilder(flatDbConfig, initConfig);
        builder.AddModule(new BuiltInStepsModule());
        builder.RegisterInstance(Substitute.For<IStateBoundary>()).As<IStateBoundary>();
        using IContainer container = builder.Build();

        DependencyResolutionException exception = Assert.Throws<DependencyResolutionException>(() => container.Resolve<ValidateFlatState>())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.GetBaseException(), Is.TypeOf<InvalidConfigurationException>());
            Assert.That(exception.ToString(), Does.Contain("FlatDb.Enabled=false is no longer supported."));
        }
    }

    [Test]
    public void FlatStateValidationStepResolvesAndPrecedesBlockTreeInitialization()
    {
        ContainerBuilder builder = CreateProductionContainerBuilder();
        builder.AddModule(new BuiltInStepsModule());
        using IContainer container = builder.Build();

        Assert.DoesNotThrow(() => container.Resolve<ValidateFlatState>());
        IEnumerable<StepInfo> steps = container.Resolve<IEnumerable<StepInfo>>();
        StepInfo validateFlatState = steps.Single(s => s.StepType == typeof(ValidateFlatState));
        StepInfo initializeBlockTree = steps.Single(s => s.StepType == typeof(InitializeBlockTree));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validateFlatState.Dependencies, Does.Contain(typeof(ApplyMemoryHint)));
            Assert.That(initializeBlockTree.Dependencies, Does.Contain(typeof(ValidateFlatState)));
        }
    }

    [TestCase("Hash")]
    [TestCase(" hash ")]
    [TestCase("HALFPATH")]
    [TestCase("0")]
    [TestCase("1")]
    [TestCase("2")]
    [TestCase("Patricia")]
    public void UnsupportedStateDbKeySchemeIsRejected(string schema)
    {
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns(schema);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(initConfig: initConfig))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.StartWith("Init.StateDbKeyScheme is unsupported:"));
            Assert.That(exception.Message, Does.Contain($"'{schema}'"));
            Assert.That(exception.Message, Does.Contain("flatdbimportfrompruningtriestate"));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase(" current ")]
    [TestCase("CURRENT")]
    public void MissingOrCurrentStateDbKeySchemeIsAllowed(string? schema)
    {
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.StateDbKeyScheme.Returns(schema);

        Assert.DoesNotThrow(() => CreatePolicy(initConfig: initConfig));
    }

    [TestCase(false, false, "FlatDb.Enabled=false is no longer supported.")]
    [TestCase(true, true, "FlatDb.ImportFromPruningTrieState=true is no longer supported.")]
    public void ExplicitLegacyFlatDbSettingIsRejected(bool enabled, bool importFromPruningTrieState, string prefix)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(enabled);
        flatDbConfig.ImportFromPruningTrieState.Returns(importFromPruningTrieState);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(flatDbConfig: flatDbConfig))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.StartWith(prefix));
            Assert.That(exception.Message, Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
        }
    }

    [Test]
    public void FlatDbDisabledByTypedConfigurationIsRejected()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(false);

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(flatDbConfig: flatDbConfig))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.StartWith("FlatDb.Enabled=false is no longer supported."));
            Assert.That(exception.Message, Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
        }
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
        CreatePolicy(
            flatDbConfig: flatDbConfig,
            availableMemoryBytes: availableMemoryGiB.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)));

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

    private static ContainerBuilder CreateProductionContainerBuilder(
        IFlatDbConfig? flatDbConfig = null,
        IInitConfig? initConfig = null,
        IFileSystem? fileSystem = null,
        IDbFactory? dbFactory = null,
        IPersistence? persistence = null)
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

        if (persistence is null)
        {
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.CurrentState.Returns(StateId.PreGenesis);
            persistence = Substitute.For<IPersistence>();
            persistence.CreateReader().Returns(reader);
        }

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

        return new ContainerBuilder()
            .AddSingleton<IFlatDbConfig>(flatDbConfig)
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo(32L * 1024 * 1024 * 1024))
            .AddSingleton<IPersistence>(persistence)
            .AddSingleton<IDbFactory>(dbFactory)
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddModule(new WorldStateModule(initConfig));
    }

    private static (IFileSystem FileSystem, IDbFactory DbFactory) CreateLegacyFileSystem(string marker, string statePath = "state")
    {
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        IDirectory directory = Substitute.For<IDirectory>();
        fileSystem.Directory.Returns(directory);
        directory.Exists(Arg.Any<string>()).Returns(true);
        directory.EnumerateFiles(Arg.Any<string>(), "*", SearchOption.AllDirectories).Returns([marker]);

        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(statePath);
        return (fileSystem, dbFactory);
    }
}
