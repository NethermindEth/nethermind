// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(flags.HasFlag(Flags.Enabled));
        flatDbConfig.ImportFromPruningTrieState.Returns(flags.HasFlag(Flags.ImportFromPruningTrieState));

        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        if (flags.HasFlag(Flags.FlatHasData))
        {
            using IColumnsWriteBatch<FlatDbColumns> batch = flatDb.StartWriteBatch();
            BasePersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));
        }

        MemDb patriciaDb = new();
        if (flags.HasFlag(Flags.PatriciaHasData))
            patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(flatDbConfig, new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance);
        Assert.That(policy.ShouldTurnOnFlatDb(), Is.EqualTo(expected));
    }

    [TestCase(false, false, false, Description = "No metadata and no patricia data")]
    [TestCase(true, false, false, Description = "No metadata and existing patricia data")]
    [TestCase(false, true, false, Description = "No metadata and import from pruning trie state")]
    [TestCase(false, false, true, Description = "Committed current state with non-Paprika layout")]
    public void ShouldTurnOnFlatDb_ThrowsForPaprikaFlatWithoutPreparedMetadata(bool patriciaHasData, bool importFromPruningTrieState, bool writeFlatLayout)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.Layout.Returns(FlatLayout.PaprikaFlat);
        flatDbConfig.ImportFromPruningTrieState.Returns(importFromPruningTrieState);

        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        if (writeFlatLayout)
        {
            using IColumnsWriteBatch<FlatDbColumns> batch = flatDb.StartWriteBatch();
            IWriteBatch metadata = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetCurrentState(metadata, new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));
            BasePersistence.SetLayout(metadata, FlatLayout.Flat);
        }

        MemDb patriciaDb = new();
        if (patriciaHasData)
        {
            patriciaDb.Set([1], [1]);
        }

        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(
            () => _ = new FlatStateActivationPolicy(flatDbConfig, new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance));
        Assert.That(exception.Message, Does.Contain("requires a prepared PaprikaFlat flat DB"));
    }

    [Test]
    public void ShouldTurnOnFlatDb_ReturnsTrue_ForPaprikaFlatCommittedState()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.Layout.Returns(FlatLayout.PaprikaFlat);

        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        using (IColumnsWriteBatch<FlatDbColumns> batch = flatDb.StartWriteBatch())
        {
            IWriteBatch metadata = batch.GetColumnBatch(FlatDbColumns.Metadata);
            BasePersistence.SetCurrentState(metadata, new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));
            BasePersistence.SetLayout(metadata, FlatLayout.PaprikaFlat);
        }

        MemDb patriciaDb = new();
        FlatStateActivationPolicy policy = new(flatDbConfig, new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
    }

    [Test]
    public void ShouldTurnOnFlatDb_ReturnsTrue_ForPaprikaFlatPendingStateWithPatriciaData()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(true);
        flatDbConfig.Layout.Returns(FlatLayout.PaprikaFlat);
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        using (IColumnsWriteBatch<FlatDbColumns> batch = flatDb.StartWriteBatch())
        {
            PaprikaFlatPersistence.SetPendingCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), new StateId(1, Nethermind.Core.Crypto.Keccak.Zero));
        }

        MemDb patriciaDb = new();
        patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(flatDbConfig, new Lazy<IColumnsDb<FlatDbColumns>>(() => flatDb), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.True);
    }

    [Test]
    public void ShouldTurnOnFlatDb_DoesNotResolveFlatDb_WhenDisabled()
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(false);

        FlatStateActivationPolicy policy = new(
            flatDbConfig,
            new Lazy<IColumnsDb<FlatDbColumns>>(() => throw new InvalidOperationException("Flat DB should not be resolved.")),
            new Lazy<IDb>(() => throw new InvalidOperationException("Patricia DB should not be resolved.")),
            LimboLogs.Instance);

        Assert.That(policy.ShouldTurnOnFlatDb(), Is.False);
    }
}
