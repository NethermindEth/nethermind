// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        PatriciaHasData = 8
    }

    // Branch 1: Enabled=false → false, regardless of db content
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(Flags.None, false, Description = "Disabled → always false")]
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

    // Soak #13577: repair left CurrentState intact ("already have state") and leftover patricia
    // state/ from a 1.39→2.0 migrate would steal the backend if Clear() fell through.
    [Test]
    public void Repaired_with_resync_stays_flat_and_clears_even_when_patricia_exists()
    {
        PolicySetup setup = CreateSetup(
            enabled: true,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: true,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            wasRepairedOnOpen: true,
            onRepair: FlatDbOnRepair.Resync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.True);
            setup.Persistence.Received(1).Clear();
            setup.Persistence.DidNotReceive().AcknowledgeRepair();
        }
    }

    [Test]
    public void Repaired_empty_flat_with_patricia_stays_patricia()
    {
        TestLogger testLogger = new();
        PolicySetup setup = CreateSetup(
            enabled: true,
            importFromPruning: false,
            flatHasData: false,
            patriciaHasData: true,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)),
            wasRepairedOnOpen: true,
            onRepair: FlatDbOnRepair.Resync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.False);
            setup.Persistence.DidNotReceive().Clear();
            setup.Persistence.Received(1).AcknowledgeRepair();
            Assert.That(testLogger.LogList.Count(static l => l.Contains("may diverge")), Is.EqualTo(1));
        }
    }

    [Test]
    public void Repaired_with_resync_clears_so_finder_sees_pregenesis()
    {
        PolicySetup setup = CreateSetup(
            enabled: true,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            wasRepairedOnOpen: true,
            onRepair: FlatDbOnRepair.Resync,
            configurePersistence: (persistence, reader) =>
                persistence.When(p => p.Clear()).Do(_ => reader.CurrentState.Returns(StateId.PreGenesis)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.True);
            setup.Persistence.Received(1).Clear();
            setup.Persistence.DidNotReceive().AcknowledgeRepair();
            Assert.That(setup.Reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
        }
    }

    [Test]
    public void Repaired_with_ignore_does_not_clear()
    {
        TestLogger testLogger = new();
        PolicySetup setup = CreateSetup(
            enabled: true,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: new OneLoggerLogManager(new ILogger(testLogger)),
            wasRepairedOnOpen: true,
            onRepair: FlatDbOnRepair.Ignore);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.True);
            setup.Persistence.DidNotReceive().Clear();
            setup.Persistence.Received(1).AcknowledgeRepair();
            Assert.That(testLogger.LogList.Count(static l => l.Contains("may diverge")), Is.EqualTo(1));
        }
    }

    [Test]
    public void Not_repaired_does_not_clear()
    {
        PolicySetup setup = CreateSetup(
            enabled: true,
            importFromPruning: false,
            flatHasData: true,
            patriciaHasData: false,
            layout: FlatLayout.Flat,
            availableMemoryBytes: 32.GiB,
            logManager: LimboLogs.Instance,
            wasRepairedOnOpen: false,
            onRepair: FlatDbOnRepair.Resync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setup.Policy.ShouldTurnOnFlatDb(), Is.True);
            setup.Persistence.DidNotReceive().Clear();
            setup.Persistence.DidNotReceive().AcknowledgeRepair();
        }
    }

    private static FlatStateActivationPolicy CreatePolicy(
        bool enabled, bool importFromPruning, bool flatHasData, bool patriciaHasData,
        FlatLayout layout, long availableMemoryBytes, ILogManager logManager)
        => CreateSetup(enabled, importFromPruning, flatHasData, patriciaHasData, layout, availableMemoryBytes, logManager).Policy;

    private readonly record struct PolicySetup(
        FlatStateActivationPolicy Policy,
        IPersistence Persistence,
        IPersistence.IPersistenceReader Reader);

    private static PolicySetup CreateSetup(
        bool enabled, bool importFromPruning, bool flatHasData, bool patriciaHasData,
        FlatLayout layout, long availableMemoryBytes, ILogManager logManager,
        bool wasRepairedOnOpen = false, FlatDbOnRepair onRepair = FlatDbOnRepair.Resync,
        Action<IPersistence, IPersistence.IPersistenceReader> configurePersistence = null)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(enabled);
        flatDbConfig.ImportFromPruningTrieState.Returns(importFromPruning);
        flatDbConfig.Layout.Returns(layout);
        flatDbConfig.OnRepair.Returns(onRepair);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flatHasData ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero) : StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);
        flatPersistence.WasRepairedOnOpen.Returns(wasRepairedOnOpen);
        configurePersistence?.Invoke(flatPersistence, reader);

        MemDb patriciaDb = new();
        if (patriciaHasData)
            patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(
            flatDbConfig,
            new TestHardwareInfo(availableMemoryBytes),
            new Lazy<IPersistence>(() => flatPersistence),
            new Lazy<IDb>(() => patriciaDb),
            logManager);

        return new PolicySetup(policy, flatPersistence, reader);
    }
}
