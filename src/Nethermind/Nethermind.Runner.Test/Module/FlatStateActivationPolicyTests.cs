// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
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
        PatriciaHasData = 8,
        WipedForSync = 16,
        Repaired = 32,
        FlatDataKeys = 64,
        FastSync = 128,
        SnapSync = 256
    }

    public enum RepairOutcome
    {
        Untouched,
        Acknowledged,
        Wiped
    }

    // Branch 1: Enabled=false, no flat directory → false
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, flat was wiped for a state sync → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 6: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(Flags.None, false, Description = "Disabled, no flat directory → false")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, true, Description = "Flat has committed state → true")]
    [TestCase(Flags.Enabled | Flags.WipedForSync | Flags.PatriciaHasData, true, Description = "Restart during the resync on a migrated node stays flat")]
    [TestCase(Flags.Enabled | Flags.WipedForSync, true, Description = "Wiped for sync, no patricia state → true")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, true, Description = "ImportFromPruningTrieState=true → true")]
    [TestCase(Flags.Enabled | Flags.PatriciaHasData, false, Description = "Patricia has data → false")]
    [TestCase(Flags.Enabled, true, Description = "Fresh node, flat enabled → true")]
    [TestCase(Flags.Enabled | Flags.FastSync | Flags.SnapSync, true, Description = "Fresh, fast + snap → true")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState | Flags.PatriciaHasData | Flags.FastSync, true, Description = "Import from patricia, fast without snap → true")]
    public void ShouldTurnOnFlatDb_ReturnsExpected(Flags flags, bool expected)
    {
        FlatStateActivationPolicy policy = CreatePolicy(
            enabled: flags.HasFlag(Flags.Enabled),
            importFromPruning: flags.HasFlag(Flags.ImportFromPruningTrieState),
            flatHasData: flags.HasFlag(Flags.FlatHasData),
            patriciaHasData: flags.HasFlag(Flags.PatriciaHasData),
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            wipedForSync: flags.HasFlag(Flags.WipedForSync),
            fastSync: flags.HasFlag(Flags.FastSync),
            snapSync: flags.HasFlag(Flags.SnapSync));

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

    // Soak #13577: repair left CurrentState intact ("already have state") and leftover patricia
    // state/ from a 1.39→2.0 migrate would steal the backend if the wipe fell through.
    [TestCase(Flags.Repaired | Flags.FlatHasData | Flags.PatriciaHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Wiped, TestName = "Repaired flat with leftover patricia resyncs")]
    [TestCase(Flags.Repaired | Flags.FlatHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Wiped, TestName = "Repaired flat resyncs")]
    [TestCase(Flags.Repaired | Flags.FlatDataKeys | Flags.PatriciaHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Wiped, TestName = "Repair that dropped the state pointer still resyncs")]
    [TestCase(Flags.Repaired | Flags.PatriciaHasData, FlatDbOnRepair.Resync, false, RepairOutcome.Acknowledged, TestName = "Repaired empty flat keeps patricia")]
    [TestCase(Flags.Repaired | Flags.WipedForSync | Flags.PatriciaHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Wiped, TestName = "Restart between wipe and acknowledge redoes the wipe")]
    [TestCase(Flags.Repaired | Flags.FlatHasData, FlatDbOnRepair.Ignore, true, RepairOutcome.Acknowledged, TestName = "Repaired flat with Ignore keeps its data")]
    [TestCase(Flags.FlatHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Untouched, TestName = "Unrepaired flat is left alone")]
    [TestCase(Flags.FlatHasData | Flags.WipedForSync | Flags.PatriciaHasData, FlatDbOnRepair.Resync, true, RepairOutcome.Wiped, TestName = "Interrupted wipe is redone")]
    public void Repaired_or_interrupted_flat_db_backend(Flags flags, FlatDbOnRepair onRepair, bool expectFlat, RepairOutcome expectedOutcome)
    {
        PolicySetup setup = CreateSetup(flags | Flags.Enabled, FlatLayout.Flat, 32.GiB, LimboLogs.Instance, onRepair);

        string[] expectedEvents = expectedOutcome switch
        {
            RepairOutcome.Wiped => [SpyFlatColumnsDb.WriteEvent, SpyFlatColumnsDb.FlushEvent, SpyFlatColumnsDb.AcknowledgeEvent],
            RepairOutcome.Acknowledged => [SpyFlatColumnsDb.AcknowledgeEvent],
            _ => []
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.EqualTo(expectFlat));
            Assert.That(setup.FlatDb.Events, Is.EqualTo(expectedEvents));
        }
    }

    [TestCase(Flags.Repaired | Flags.PatriciaHasData, FlatDbOnRepair.Resync, "holds no state; the patricia backend stays active")]
    [TestCase(Flags.Repaired | Flags.FlatHasData, FlatDbOnRepair.Ignore, "may diverge")]
    [TestCase(Flags.Repaired | Flags.FlatHasData | Flags.WipedForSync, FlatDbOnRepair.Resync, "interrupted flat DB wipe was detected after a RocksDB auto-repair")]
    public void Repair_logs_what_the_policy_did(Flags flags, FlatDbOnRepair onRepair, string expectedLog)
    {
        TestLogger testLogger = new();
        PolicySetup setup = CreateSetup(flags | Flags.Enabled, FlatLayout.Flat, 32.GiB, new OneLoggerLogManager(new ILogger(testLogger)), onRepair);

        setup.Policy.ShouldTurnOnFlatDb();

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
        PolicySetup setup = CreateSetup(Flags.Enabled | Flags.Repaired | Flags.FlatHasData, FlatLayout.Flat, 32.GiB,
            new OneLoggerLogManager(new ILogger(testLogger)), onRepair, historyEnabled, retention);

        setup.Policy.ShouldTurnOnFlatDb();

        Assert.That(testLogger.LogList.Count(static l => l.Contains("flatHistory DB was not wiped")), Is.EqualTo(expectWarn ? 1 : 0));
    }

    // ImportFlatDb skips when patricia holds no state, so the import flag alone must not exempt a fresh node.
    [TestCase(false, Description = "Fresh node")]
    [TestCase(true, Description = "Import flag set, but patricia is empty")]
    public void Fresh_flat_with_fast_sync_and_no_snap_is_refused(bool importFromPruning)
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => CreatePolicy(
            enabled: true,
            importFromPruning: importFromPruning,
            flatHasData: false,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            fastSync: true,
            snapSync: false))!;

        Assert.That(ex.Message, Does.Contain("SnapSync").And.Contain("FlatDb.Enabled"));
    }

    // A wipe leaves nothing to keep serving, so the refusal must come first or the node loses its state and still cannot sync.
    // The remedy must be one that boots the node: Enabled=false is refused on an existing flat DB, and only a repair
    // that kept the state pointer can be booted with OnRepair=Ignore.
    [TestCase(Flags.Repaired | Flags.FlatHasData, "FlatDb.OnRepair=Ignore", Description = "Repaired flat")]
    [TestCase(Flags.Repaired, "holds no state", Description = "Repaired flat, state pointer dropped")]
    [TestCase(Flags.FlatHasData | Flags.WipedForSync, "holds no state", Description = "Interrupted wipe")]
    [TestCase(Flags.WipedForSync, "holds no state", Description = "Resync in progress")]
    public void Resync_with_fast_sync_and_no_snap_is_refused_before_the_wipe(Flags flags, string remedy)
    {
        SpyFlatColumnsDb flatDb = new() { WasRepairedOnOpen = flags.HasFlag(Flags.Repaired) };

        using (Assert.EnterMultipleScope())
        {
            InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => CreateSetup(flags | Flags.Enabled | Flags.FastSync, FlatLayout.Flat, 32.GiB, LimboLogs.Instance, flatDb: flatDb))!;
            Assert.That(ex.Message, Does.Contain(remedy).And.Not.Contain("FlatDb.Enabled"));
            Assert.That(flatDb.Events, Is.Empty);
        }
    }

    // The DB layer roots a relative BaseDbPath at the executing directory; the probe must resolve it the same way.
    [TestCase("/data")]
    [TestCase("nethermind_db/mainnet")]
    public void Disabling_flat_on_an_existing_flat_db_is_refused(string baseDbPath)
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => CreatePolicy(
            enabled: false,
            importFromPruning: false,
            flatHasData: false,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            sstFiles: ["state.sst"],
            baseDbPath: baseDbPath))!;

        Assert.That(ex.Message, Does.Contain("FlatDb.Enabled").And.Contain("existing"));
    }

    // RocksDB writes CURRENT when opening an empty DB, so a flat directory without SST files holds no state.
    [Test]
    public void Disabling_flat_with_an_empty_flat_directory_stays_on_patricia() =>
        Assert.That(CreatePolicy(enabled: false, importFromPruning: false, flatHasData: false, patriciaHasData: false,
            layout: FlatLayout.Flat, availableMemoryBytes: 32.GiB, logManager: LimboLogs.Instance, sstFiles: []).ShouldTurnOnFlatDb(), Is.False);

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
        FlatLayout layout, long availableMemoryBytes, ILogManager logManager, bool wipedForSync = false,
        bool fastSync = false, bool snapSync = false, string[] sstFiles = null, string baseDbPath = "/data")
    {
        Flags flags = (enabled ? Flags.Enabled : Flags.None)
            | (importFromPruning ? Flags.ImportFromPruningTrieState : Flags.None)
            | (flatHasData ? Flags.FlatHasData : Flags.None)
            | (patriciaHasData ? Flags.PatriciaHasData : Flags.None)
            | (wipedForSync ? Flags.WipedForSync : Flags.None)
            | (fastSync ? Flags.FastSync : Flags.None)
            | (snapSync ? Flags.SnapSync : Flags.None);
        return CreateSetup(flags, layout, availableMemoryBytes, logManager, sstFiles: sstFiles, baseDbPath: baseDbPath).Policy;
    }

    private readonly record struct PolicySetup(FlatStateActivationPolicy Policy, SpyFlatColumnsDb FlatDb);

    private static PolicySetup CreateSetup(Flags flags, FlatLayout layout, long availableMemoryBytes, ILogManager logManager,
        FlatDbOnRepair onRepair = FlatDbOnRepair.Resync, bool historyEnabled = false, HistoryRetentionMode historyRetention = HistoryRetentionMode.None,
        string[] sstFiles = null, string baseDbPath = "/data", SpyFlatColumnsDb flatDb = null)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(flags.HasFlag(Flags.Enabled));
        flatDbConfig.HistoryEnabled.Returns(historyEnabled);
        flatDbConfig.HistoryRetention.Returns(historyRetention);
        flatDbConfig.ImportFromPruningTrieState.Returns(flags.HasFlag(Flags.ImportFromPruningTrieState));
        flatDbConfig.Layout.Returns(layout);
        flatDbConfig.OnRepair.Returns(onRepair);

        flatDb ??= new() { WasRepairedOnOpen = flags.HasFlag(Flags.Repaired) };
        if (flags.HasFlag(Flags.FlatHasData))
            new RocksDbPersistence(flatDb, LimboLogs.Instance).CreateWriteBatch(StateId.PreGenesis, new StateId(1, Keccak.Zero), WriteFlags.None).Dispose();
        if (flags.HasFlag(Flags.WipedForSync))
            MarkWipedForSync(flatDb);
        if (flags.HasFlag(Flags.FlatDataKeys))
            flatDb.GetColumnDb(FlatDbColumns.Storage).Set([1], [1]);
        flatDb.Events.Clear();

        MemDb patriciaDb = new();
        if (flags.HasFlag(Flags.PatriciaHasData))
            patriciaDb.Set([1], [1]);

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.FastSync.Returns(flags.HasFlag(Flags.FastSync));
        syncConfig.SnapSync.Returns(flags.HasFlag(Flags.SnapSync));

        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.BaseDbPath.Returns(baseDbPath);
        IDirectory directory = Substitute.For<IDirectory>();
        string flatPath = DbOnTheRocks.GetFullDbPath(DbNames.Flat, baseDbPath);
        directory.EnumerateFiles(Arg.Any<string>(), "*.sst").Returns(call =>
            sstFiles is not null && call.ArgAt<string>(0) == flatPath
                ? sstFiles
                : throw new DirectoryNotFoundException(call.ArgAt<string>(0)));
        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Directory.Returns(directory);

        FlatStateActivationPolicy policy = new(
            flatDbConfig,
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb),
            new Lazy<IDb>(() => patriciaDb),
            syncConfig,
            initConfig,
            fileSystem,
            logManager);

        return new PolicySetup(policy, flatDb);
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
