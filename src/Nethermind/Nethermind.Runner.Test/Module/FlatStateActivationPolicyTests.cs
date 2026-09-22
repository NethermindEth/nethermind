// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatStateActivationPolicyTests
{
    [Flags]
    public enum Flags
    {
        None = 0,
        Enabled = 1,
        FlatHasData = 2,
        ImportFromPruningTrieState = 4,
        PatriciaHasData = 8
    }

    // Branch 1: Enabled=false, no flat directory → false
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(Flags.None, false, Description = "Disabled, no flat directory → false")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, true, Description = "Flat has committed state → true")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, true, Description = "ImportFromPruningTrieState=true → true")]
    [TestCase(Flags.Enabled | Flags.PatriciaHasData, false, Description = "Patricia has data → false")]
    [TestCase(Flags.Enabled, true, Description = "Fresh node, flat enabled → true")]
    public void ShouldTurnOnFlatDb_ReturnsExpected(Flags flags, bool expected)
    {
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: flags.HasFlag(Flags.Enabled),
            importFromPruning: flags.HasFlag(Flags.ImportFromPruningTrieState),
            flatHasData: flags.HasFlag(Flags.FlatHasData),
            patriciaHasData: flags.HasFlag(Flags.PatriciaHasData),
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.EqualTo(expected));
    }

    // Advisory fires only when flat is actually activated, the layout is not FlatInTrie, and available memory < 16 GB.
    [TestCase(true, FlatLayout.Flat, 8, true, Description = "Flat active, Flat layout, low RAM → warn")]
    [TestCase(true, FlatLayout.FlatInTrie, 8, false, Description = "Already FlatInTrie → no warn")]
    [TestCase(true, FlatLayout.Flat, 32, false, Description = "Ample RAM → no warn")]
    [TestCase(false, FlatLayout.Flat, 8, false, Description = "Flat disabled (patricia) → no warn")]
    public void AdvisesFlatInTrieLayout_OnlyWhenLowMemoryAndFlatActive(bool enabled, FlatLayout layout, int availableMemoryGiB, bool expectWarn)
    {
        TestLogger testLogger = new();
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: enabled,
            importFromPruning: false,
            flatHasData: false,
            patriciaHasData: false,
            layout: layout,
            availableMemoryBytes: availableMemoryGiB.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)));

        bool warned = testLogger.LogList.Any(l => l.Contains("--FlatDb.Layout") && l.Contains(nameof(FlatLayout.FlatInTrie)));
        Assert.That(warned, Is.EqualTo(expectWarn));
    }

    [Test]
    public void Fresh_flat_with_fast_sync_and_no_snap_is_refused()
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => CreatePolicy(
            enabled: true,
            importFromPruning: false,
            flatHasData: false,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            fastSync: true,
            snapSync: false))!;

        Assert.That(ex.Message, Does.Contain("SnapSync").And.Contain("FlatDb.Enabled"));
    }

    [Test]
    public void Fresh_flat_with_fast_and_snap_is_allowed()
    {
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: true,
            importFromPruning: false,
            flatHasData: false,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            fastSync: true,
            snapSync: true);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
    }

    [Test]
    public void Import_from_patricia_with_fast_sync_and_no_snap_is_allowed()
    {
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: true,
            importFromPruning: true,
            flatHasData: false,
            patriciaHasData: true,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            fastSync: true,
            snapSync: false);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
    }

    // The DB layer roots a relative BaseDbPath at the executing directory; the probe must resolve it the same way.
    [TestCase("/data")]
    [TestCase("nethermind_db/mainnet")]
    public void Disabling_flat_on_an_existing_flat_db_is_refused(string baseDbPath)
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => CreatePolicy(
            enabled: false,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            flatDirectoryExists: true,
            baseDbPath: baseDbPath))!;

        Assert.That(ex.Message, Does.Contain("FlatDb.Enabled").And.Contain("existing"));
    }

    [Test]
    public void Existing_flat_with_fast_sync_and_no_snap_keeps_serving()
    {
        TestLogger testLogger = new();
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: true,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)),
            fastSync: true,
            snapSync: false);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
        Assert.That(testLogger.LogList.Any(static l => l.Contains("SnapSync=false") && l.Contains("already has flat state")), Is.True);
    }

    private static FlatStateActivationPolicy CreatePolicy(
        bool enabled, bool importFromPruning, bool flatHasData, bool patriciaHasData,
        FlatLayout layout, long availableMemoryBytes, ILogManager logManager,
        bool fastSync = false, bool snapSync = false, bool flatDirectoryExists = false, string baseDbPath = "/data")
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(enabled);
        flatDbConfig.ImportFromPruningTrieState.Returns(importFromPruning);
        flatDbConfig.Layout.Returns(layout);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flatHasData ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero) : StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);

        MemDb patriciaDb = new();
        if (patriciaHasData)
            patriciaDb.Set([1], [1]);

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.FastSync.Returns(fastSync);
        syncConfig.SnapSync.Returns(snapSync);

        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.BaseDbPath.Returns(baseDbPath);
        IDirectory directory = Substitute.For<IDirectory>();
        string flatPath = DbNames.Flat.GetApplicationResourcePath(baseDbPath);
        directory.EnumerateFiles(Arg.Any<string>(), "*.sst").Returns(call =>
            flatDirectoryExists && call.ArgAt<string>(0) == flatPath
                ? ["state.sst"]
                : throw new DirectoryNotFoundException(call.ArgAt<string>(0)));
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Directory.Returns(directory);

        return new FlatStateActivationPolicy(
            flatDbConfig,
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IPersistence>(() => flatPersistence),
            new Lazy<IDb>(() => patriciaDb),
            syncConfig,
            initConfig,
            fileSystem,
            logManager);
    }
}
