// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
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
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.ImportFromPruningTrieState, true, Description = "Import finished and left configured -> drop; the fallback boundary stops reading the trie once flat is populated")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.ImportFromPruningTrieState, false, Description = "Mid-import, flat still empty -> keep")]
    public void Drops_only_when_flat_owns_a_populated_store(Flags flags, bool expected)
    {
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(flags.HasFlag(Flags.FlatHasData)),
            () => LimboLogs.Instance);

        Assert.That(dropped, Is.EqualTo(expected));
    }

    [Test]
    public void Keeps_the_trie_when_nothing_registered_the_flat_config()
    {
        // The state DB is registered in containers that know nothing about the flat backend.
        // Resolving IFlatDbConfig there is not merely useless, it breaks the registration.
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            null,
            () => throw new AssertionException("the flat persistence must not be resolved"),
            () => throw new AssertionException("the log manager must not be resolved"));

        Assert.That(dropped, Is.False);
    }

    [Test]
    public void Warns_before_dropping()
    {
        TestLogger logger = new();
        Flags flags = Flags.Drop | Flags.Enabled | Flags.FlatHasData;

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(true),
            () => new OneLoggerLogManager(new ILogger(logger)));

        Assert.That(logger.LogList.Any(l => l.Contains("irreversible", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void Declining_is_not_a_warning()
    {
        // TestLogger flattens every level into one list, so the level itself needs a substitute.
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.IsInfo.Returns(true);

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(Flags.Drop | Flags.FlatHasData),
            () => Persistence(true),
            () => new OneLoggerLogManager(new ILogger(logger)));

        logger.DidNotReceive().Warn(Arg.Any<string>());
        logger.Received().Info(Arg.Any<string>());
    }

    [Test]
    public void Does_not_touch_the_flat_store_when_the_flag_is_off()
    {
        bool resolved = false;

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(Flags.Enabled | Flags.FlatHasData),
            () => { resolved = true; return Persistence(true); },
            () => LimboLogs.Instance);

        Assert.That(resolved, Is.False, "the flat persistence must not be resolved unless the drop is actually requested");
    }

    [Test]
    public void State_db_still_resolves_when_the_drop_is_requested()
    {
        // The state DB factory resolves the flat IPersistence to decide, so a real container is the only
        // place a dependency cycle would show up.
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = true;
                cfg.DropPruningTrieState = true;
            })
            .Build();

        Assert.That(() => container.ResolveKeyed<IDb>(DbNames.State), Throws.Nothing);
    }

    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData, true)]
    [TestCase(Flags.Drop | Flags.FlatHasData, false)]
    [TestCase(Flags.Enabled | Flags.FlatHasData, false)]
    [TestCase(Flags.Drop | Flags.Enabled, false)]
    public void State_db_is_opened_with_DeleteOnStart_only_when_dropping(Flags flags, bool expected)
    {
        // MemDbFactory ignores DeleteOnStart, so the settings handed to the factory are the only evidence.
        DbSettings stateDbSettings = null;
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.CreateDb(Arg.Do<DbSettings>(settings => stateDbSettings = settings)).Returns(new MemDb());
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(ci => ci.Arg<DbSettings>().DbPath);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = flags.HasFlag(Flags.Enabled);
                cfg.DropPruningTrieState = flags.HasFlag(Flags.Drop);
            })
            .AddSingleton<IPersistence>(Persistence(flags.HasFlag(Flags.FlatHasData)))
            .AddSingleton<IDbFactory>(dbFactory)
            .Build();

        container.ResolveKeyed<IDb>(DbNames.State);

        Assert.That(stateDbSettings?.DeleteOnStart, Is.EqualTo(expected));
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
