// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
        PatriciaHasData = 8,
        WipedForSync = 16,
        Repaired = 32,
        FlatDataKeys = 64
    }

    public enum RepairOutcome
    {
        Untouched,
        Acknowledged,
        Wiped
    }

    // Branch 1: Enabled=false → false, regardless of db content
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, flat was wiped for a state sync → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 6: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(Flags.None, false, Description = "Disabled → always false")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, true, Description = "Flat has committed state → true")]
    [TestCase(Flags.Enabled | Flags.WipedForSync | Flags.PatriciaHasData, true, Description = "Restart during the resync on a migrated node stays flat")]
    [TestCase(Flags.Enabled | Flags.WipedForSync, true, Description = "Wiped for sync, no patricia state → true")]
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
            logManager: LimboLogs.Instance,
            wipedForSync: flags.HasFlag(Flags.WipedForSync));

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
            RepairOutcome.Wiped => [SpyFlatColumnsDb.ClearEvent, SpyFlatColumnsDb.FlushEvent, SpyFlatColumnsDb.AcknowledgeEvent],
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
    public void Repair_that_keeps_the_data_logs_why(Flags flags, FlatDbOnRepair onRepair, string expectedLog)
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

    private static FlatStateActivationPolicy CreatePolicy(
        bool enabled, bool importFromPruning, bool flatHasData, bool patriciaHasData,
        FlatLayout layout, long availableMemoryBytes, ILogManager logManager, bool wipedForSync = false)
    {
        Flags flags = (enabled ? Flags.Enabled : Flags.None)
            | (importFromPruning ? Flags.ImportFromPruningTrieState : Flags.None)
            | (flatHasData ? Flags.FlatHasData : Flags.None)
            | (patriciaHasData ? Flags.PatriciaHasData : Flags.None)
            | (wipedForSync ? Flags.WipedForSync : Flags.None);
        return CreateSetup(flags, layout, availableMemoryBytes, logManager).Policy;
    }

    private readonly record struct PolicySetup(FlatStateActivationPolicy Policy, SpyFlatColumnsDb FlatDb);

    private static PolicySetup CreateSetup(Flags flags, FlatLayout layout, long availableMemoryBytes, ILogManager logManager,
        FlatDbOnRepair onRepair = FlatDbOnRepair.Resync)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(flags.HasFlag(Flags.Enabled));
        flatDbConfig.ImportFromPruningTrieState.Returns(flags.HasFlag(Flags.ImportFromPruningTrieState));
        flatDbConfig.Layout.Returns(layout);
        flatDbConfig.OnRepair.Returns(onRepair);

        SpyFlatColumnsDb flatDb = new() { WasRepairedOnOpen = flags.HasFlag(Flags.Repaired) };
        if (flags.HasFlag(Flags.WipedForSync))
            new RocksDbPersistence(flatDb, LimboLogs.Instance).Clear();
        if (flags.HasFlag(Flags.FlatDataKeys))
            flatDb.GetColumnDb(FlatDbColumns.Account).Set([1], [1]);
        flatDb.Events.Clear();

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flags.HasFlag(Flags.FlatHasData) ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero) : StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);
        flatPersistence.When(static p => p.Clear()).Do(_ => flatDb.Events.Add(SpyFlatColumnsDb.ClearEvent));

        MemDb patriciaDb = new();
        if (flags.HasFlag(Flags.PatriciaHasData))
            patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(
            flatDbConfig,
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IPersistence>(() => flatPersistence),
            new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb),
            new Lazy<IDb>(() => patriciaDb),
            logManager);

        return new PolicySetup(policy, flatDb);
    }

    /// <summary>A flat columns DB that reports a configurable repair flag and records the wipe, flush and acknowledge calls in order.</summary>
    public sealed class SpyFlatColumnsDb : SnapshotableMemColumnsDb<FlatDbColumns>, IDbMeta
    {
        public const string ClearEvent = "clear";
        public const string FlushEvent = "flush";
        public const string AcknowledgeEvent = "acknowledge";

        public List<string> Events { get; } = [];

        public bool WasRepairedOnOpen { get; init; }

        void IDbMeta.Flush(bool onlyWal)
        {
            Events.Add(FlushEvent);
            Flush(onlyWal);
        }

        void IDbMeta.AcknowledgeRepair() => Events.Add(AcknowledgeEvent);
    }
}
