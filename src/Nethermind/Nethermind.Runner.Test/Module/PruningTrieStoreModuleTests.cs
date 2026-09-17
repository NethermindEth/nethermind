// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PruningTrieStoreModuleTests
{
    [Flags]
    public enum Flags
    {
        None = 0,
        Drop = 1,
        Enabled = 2,
        FlatHasData = 4,
        ImportFromPruningTrieState = 8
    }

    [TestCase(Flags.None, false, Description = "Flag off -> keep")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, false, Description = "Flag off even with flat in charge -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData, true, Description = "Flat owns a populated store -> drop")]
    [TestCase(Flags.Drop | Flags.FlatHasData, false, Description = "Flat DB disabled, node runs on patricia -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled, false, Description = "Flat store still empty -> keep, or the node loses all state")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.ImportFromPruningTrieState, false, Description = "Import still configured -> keep, the importer and the fallback boundary read the trie")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.ImportFromPruningTrieState, false, Description = "Mid-import -> keep")]
    public void Drops_only_when_flat_owns_a_populated_store(Flags flags, bool expected)
    {
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(flags.HasFlag(Flags.FlatHasData)),
            LimboLogs.Instance);

        Assert.That(dropped, Is.EqualTo(expected));
    }

    [Test]
    public void Warns_before_dropping()
    {
        TestLogger logger = new();
        Flags flags = Flags.Drop | Flags.Enabled | Flags.FlatHasData;

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(true),
            new OneLoggerLogManager(new ILogger(logger)));

        Assert.That(logger.LogList.Any(l => l.Contains("irreversible", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void Does_not_touch_the_flat_store_when_the_flag_is_off()
    {
        bool resolved = false;

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(Flags.Enabled | Flags.FlatHasData),
            () => { resolved = true; return Persistence(true); },
            LimboLogs.Instance);

        Assert.That(resolved, Is.False, "the flat persistence must not be resolved unless the drop is actually requested");
    }

    private static IFlatDbConfig Config(Flags flags)
    {
        IFlatDbConfig config = Substitute.For<IFlatDbConfig>();
        config.DropPruningTrieState.Returns(flags.HasFlag(Flags.Drop));
        config.Enabled.Returns(flags.HasFlag(Flags.Enabled));
        config.ImportFromPruningTrieState.Returns(flags.HasFlag(Flags.ImportFromPruningTrieState));
        return config;
    }

    private static IPersistence Persistence(bool hasData)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(hasData
            ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero)
            : StateId.PreGenesis);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);
        return persistence;
    }
}
