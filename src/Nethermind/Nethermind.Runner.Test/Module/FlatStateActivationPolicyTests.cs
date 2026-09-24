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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
            flatState: new StateId(1, Keccak.Zero));

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

        ValidateFlatState step = null!;
        Assert.DoesNotThrow(() => step = container.Resolve<ValidateFlatState>());
        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() => step.Execute(default))!;
        Assert.That(exception.Message, Does.Contain("FlatDb.Enabled=false is no longer supported."));
    }

    [Test]
    public void FlatStateValidationStepResolvesAndPrecedesBlockTreeInitialization()
    {
        ContainerBuilder builder = CreateProductionContainerBuilder();
        builder.AddModule(new BuiltInStepsModule());
        using IContainer container = builder.Build();

        Assert.DoesNotThrow(() => container.Resolve<ValidateFlatState>().Execute(default));
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

    public enum RepairOutcome
    {
        Untouched,
        Acknowledged,
        Wiped
    }

    // Soak #13577: repair left CurrentState intact ("already have state"), and leftover legacy state files from a
    // migration must not block the resync once the flat DB owns the node's state.
    [TestCase(true, true, false, false, true, FlatDbOnRepair.Resync, RepairOutcome.Wiped, TestName = "Repaired flat with leftover legacy files resyncs")]
    [TestCase(true, true, false, false, false, FlatDbOnRepair.Resync, RepairOutcome.Wiped, TestName = "Repaired flat resyncs")]
    [TestCase(true, false, false, true, true, FlatDbOnRepair.Resync, RepairOutcome.Wiped, TestName = "Repair that dropped the state pointer still resyncs")]
    [TestCase(true, false, false, false, false, FlatDbOnRepair.Resync, RepairOutcome.Acknowledged, TestName = "Repaired empty flat is acknowledged")]
    [TestCase(true, false, true, false, true, FlatDbOnRepair.Resync, RepairOutcome.Wiped, TestName = "Restart between wipe and acknowledge redoes the wipe")]
    [TestCase(true, true, false, false, false, FlatDbOnRepair.Ignore, RepairOutcome.Acknowledged, TestName = "Repaired flat with Ignore keeps its data")]
    [TestCase(false, true, false, false, false, FlatDbOnRepair.Resync, RepairOutcome.Untouched, TestName = "Unrepaired flat is left alone")]
    [TestCase(false, true, true, false, true, FlatDbOnRepair.Resync, RepairOutcome.Wiped, TestName = "Interrupted wipe is redone")]
    [TestCase(false, false, true, false, true, FlatDbOnRepair.Resync, RepairOutcome.Untouched, TestName = "Restart during the resync on a migrated node stays on flat")]
    public void Repaired_or_interrupted_flat_db(bool repaired, bool flatHasData, bool wipedForSync, bool flatDataKeys, bool legacyFiles, FlatDbOnRepair onRepair, RepairOutcome expectedOutcome)
    {
        SpyFlatColumnsDb flatDb = CreateFlatDb(repaired, flatHasData ? new StateId(1, Keccak.Zero) : null, wipedForSync, flatDataKeys);
        IFileSystem? fileSystem = null;
        IDbFactory? dbFactory = null;
        if (legacyFiles)
            (fileSystem, dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");

        Assert.DoesNotThrow(() => CreatePolicy(flatDbConfig: CreateFlatDbConfig(onRepair), fileSystem: fileSystem, dbFactory: dbFactory, flatDb: flatDb));

        string[] expectedEvents = expectedOutcome switch
        {
            RepairOutcome.Wiped => [SpyFlatColumnsDb.WriteEvent, SpyFlatColumnsDb.FlushEvent, SpyFlatColumnsDb.AcknowledgeEvent],
            RepairOutcome.Acknowledged => [SpyFlatColumnsDb.AcknowledgeEvent],
            _ => []
        };
        Assert.That(flatDb.Events, Is.EqualTo(expectedEvents));
    }

    [Test]
    public void Repaired_empty_flat_with_legacy_files_is_rejected_like_a_fresh_node()
    {
        SpyFlatColumnsDb flatDb = CreateFlatDb(repaired: true);
        (IFileSystem fileSystem, IDbFactory dbFactory) = CreateLegacyFileSystem("state/0/MANIFEST-000001");

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() =>
            CreatePolicy(fileSystem: fileSystem, dbFactory: dbFactory, flatDb: flatDb))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain(FlatStateActivationPolicy.LegacySchemaMessage));
            Assert.That(flatDb.Events, Is.EqualTo(new[] { SpyFlatColumnsDb.AcknowledgeEvent }));
        }
    }

    [TestCase(true, false, false, FlatDbOnRepair.Resync, "holds no state; the repair is acknowledged")]
    [TestCase(true, true, false, FlatDbOnRepair.Ignore, "may diverge")]
    [TestCase(true, true, true, FlatDbOnRepair.Resync, "interrupted flat DB wipe was detected after a RocksDB auto-repair")]
    public void Repair_logs_what_the_policy_did(bool repaired, bool flatHasData, bool wipedForSync, FlatDbOnRepair onRepair, string expectedLog)
    {
        TestLogger testLogger = new();
        SpyFlatColumnsDb flatDb = CreateFlatDb(repaired, flatHasData ? new StateId(1, Keccak.Zero) : null, wipedForSync);

        CreatePolicy(flatDbConfig: CreateFlatDbConfig(onRepair), flatDb: flatDb, logManager: new OneLoggerLogManager(new ILogger(testLogger)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testLogger.LogList.Count(l => l.Contains(expectedLog)), Is.EqualTo(1));
            Assert.That(testLogger.LogList.Count(static l => l.Contains("may diverge")), Is.EqualTo(onRepair == FlatDbOnRepair.Ignore ? 1 : 0));
        }
    }

    [TestCase(true, HistoryRetentionMode.Rolling, FlatDbOnRepair.Resync, true)]
    [TestCase(true, HistoryRetentionMode.SinceBlock, FlatDbOnRepair.Resync, true)]
    [TestCase(true, HistoryRetentionMode.None, FlatDbOnRepair.Resync, false)]
    [TestCase(false, HistoryRetentionMode.Rolling, FlatDbOnRepair.Resync, false)]
    [TestCase(true, HistoryRetentionMode.Rolling, FlatDbOnRepair.Ignore, false)]
    public void Wipe_warns_to_wipe_flat_history_only_when_windowed(bool historyEnabled, HistoryRetentionMode retention, FlatDbOnRepair onRepair, bool expectWarn)
    {
        TestLogger testLogger = new();
        IFlatDbConfig flatDbConfig = CreateFlatDbConfig(onRepair);
        flatDbConfig.HistoryEnabled.Returns(historyEnabled);
        flatDbConfig.HistoryRetention.Returns(retention);

        CreatePolicy(
            flatDbConfig: flatDbConfig,
            flatDb: CreateFlatDb(repaired: true, flatState: new StateId(1, Keccak.Zero)),
            logManager: new OneLoggerLogManager(new ILogger(testLogger)));

        Assert.That(testLogger.LogList.Count(static l => l.Contains("flatHistory DB was not wiped")), Is.EqualTo(expectWarn ? 1 : 0));
    }

    // FlatDB is the only backend, so fast sync without snap has no patricia fallback: TreeSync stays available for
    // chains whose peers do not serve snap, with a warning about the holes a restart during the sync can leave (#13575).
    [TestCase(false, false, true, "legacy TreeSync")]
    [TestCase(true, false, true, "already has flat state")]
    [TestCase(false, true, false, null)]
    [TestCase(true, true, false, null)]
    public void Fast_sync_without_snap_warns(bool flatHasData, bool snapSync, bool expectWarn, string? expectedLog)
    {
        TestLogger testLogger = new();
        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.FastSync.Returns(true);
        syncConfig.SnapSync.Returns(snapSync);

        Assert.DoesNotThrow(() => CreatePolicy(
            syncConfig: syncConfig,
            flatDb: CreateFlatDb(flatState: flatHasData ? new StateId(1, Keccak.Zero) : null),
            logManager: new OneLoggerLogManager(new ILogger(testLogger))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testLogger.LogList.Count(static l => l.Contains("SnapSync=false")), Is.EqualTo(expectWarn ? 1 : 0));
            if (expectedLog is not null)
                Assert.That(testLogger.LogList.Any(l => l.Contains(expectedLog)), Is.True);
        }
    }

    [Test]
    public void Repaired_flat_with_fast_sync_and_no_snap_still_wipes_for_the_resync()
    {
        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.FastSync.Returns(true);
        SpyFlatColumnsDb flatDb = CreateFlatDb(repaired: true, flatState: new StateId(1, Keccak.Zero));

        CreatePolicy(syncConfig: syncConfig, flatDb: flatDb);

        Assert.That(flatDb.Events, Is.EqualTo(new[] { SpyFlatColumnsDb.WriteEvent, SpyFlatColumnsDb.FlushEvent, SpyFlatColumnsDb.AcknowledgeEvent }));
    }

    private static FlatStateActivationPolicy CreatePolicy(
        IFlatDbConfig? flatDbConfig = null,
        IInitConfig? initConfig = null,
        IFileSystem? fileSystem = null,
        IDbFactory? dbFactory = null,
        StateId? flatState = null,
        long availableMemoryBytes = 32L * 1024 * 1024 * 1024,
        ILogManager? logManager = null,
        ISyncConfig? syncConfig = null,
        IColumnsDb<FlatDbColumns>? flatDb = null)
    {
        flatDbConfig ??= CreateFlatDbConfig();

        if (initConfig is null)
        {
            initConfig = Substitute.For<IInitConfig>();
            initConfig.StateDbKeyScheme.Returns("Current");
        }

        IColumnsDb<FlatDbColumns> columnsDb = flatDb ?? CreateFlatDb(flatState: flatState);

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
            syncConfig ?? Substitute.For<ISyncConfig>(),
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IColumnsDb<FlatDbColumns>>(() => columnsDb),
            dbFactory,
            fileSystem,
            logManager ?? LimboLogs.Instance);
    }

    private static IFlatDbConfig CreateFlatDbConfig(FlatDbOnRepair onRepair = FlatDbOnRepair.Resync)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.ImportFromPruningTrieState.Returns(false);
        flatDbConfig.Layout.Returns(FlatLayout.Flat);
        flatDbConfig.OnRepair.Returns(onRepair);
        return flatDbConfig;
    }

    private static SpyFlatColumnsDb CreateFlatDb(bool repaired = false, StateId? flatState = null, bool wipedForSync = false, bool flatDataKeys = false)
    {
        SpyFlatColumnsDb flatDb = new() { WasRepairedOnOpen = repaired };
        if (flatState is { } state)
            new RocksDbPersistence(flatDb, LimboLogs.Instance).CreateWriteBatch(StateId.PreGenesis, state, WriteFlags.None).Dispose();
        if (wipedForSync)
            MarkWipedForSync(flatDb);
        if (flatDataKeys)
            flatDb.GetColumnDb(FlatDbColumns.Storage).Set([1], [1]);
        flatDb.Events.Clear();
        return flatDb;
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
            .AddSingleton<IColumnsDb<FlatDbColumns>>(new MemColumnsDb<FlatDbColumns>())
            .AddSingleton<ISyncConfig>(Substitute.For<ISyncConfig>())
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

    /// <summary>Copies the wipe marker from a wiped scratch DB, so it lands without the wipe dropping the state pointer.</summary>
    private static void MarkWipedForSync(IColumnsDb<FlatDbColumns> flatDb)
    {
        MemColumnsDb<FlatDbColumns> scratch = new();
        BasePersistence.ClearAllColumns(scratch);
        IDb metadata = flatDb.GetColumnDb(FlatDbColumns.Metadata);
        foreach (KeyValuePair<byte[], byte[]> entry in scratch.GetColumnDb(FlatDbColumns.Metadata).GetAll())
            metadata.Set(entry.Key, entry.Value);
    }

    /// <summary>A flat columns DB that reports a configurable repair flag and records the write batch, flush and acknowledge calls in order.</summary>
    public sealed class SpyFlatColumnsDb : SnapshotableMemColumnsDb<FlatDbColumns>, IColumnsDb<FlatDbColumns>, IDbMeta
    {
        public const string WriteEvent = "write";
        public const string FlushEvent = "flush";
        public const string AcknowledgeEvent = "acknowledge";

        public List<string> Events { get; } = [];

        public bool WasRepairedOnOpen { get; init; }

        IColumnsWriteBatch<FlatDbColumns> IColumnsDb<FlatDbColumns>.StartWriteBatch()
        {
            Events.Add(WriteEvent);
            return StartWriteBatch();
        }

        void IDbMeta.Flush(bool onlyWal)
        {
            Events.Add(FlushEvent);
            Flush(onlyWal);
        }

        void IDbMeta.AcknowledgeRepair() => Events.Add(AcknowledgeEvent);
    }
}
