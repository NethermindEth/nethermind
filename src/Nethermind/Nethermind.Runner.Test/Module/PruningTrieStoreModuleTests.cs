// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
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
    public void Drops_only_when_flat_owns_a_populated_store(Flags flags, bool expected)
    {
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(flags.HasFlag(Flags.FlatHasData)),
            () => LimboLogs.Instance,
            () => true);

        Assert.That(dropped, Is.EqualTo(expected));
    }

    [Test]
    public void Declines_before_touching_anything_unless_the_flag_is_set([Values] bool configRegistered)
    {
        // The state DB is registered in containers that know nothing about the flat backend, so a hard Resolve
        // of IFlatDbConfig would break them, and nothing beyond the flag may be resolved until it is known to be set.
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            configRegistered ? Config(Flags.Enabled | Flags.FlatHasData) : null,
            () => throw new AssertionException("the flat persistence must not be resolved"),
            () => throw new AssertionException("the log manager must not be resolved"),
            () => throw new AssertionException("the disk must not be touched"));

        Assert.That(dropped, Is.False);
    }

    [TestCase(true, Description = "Trie data on disk (a first drop, or one cut short) -> warn, that is what gets lost")]
    [TestCase(false, Description = "Already empty (every start after a completed drop) -> nothing to lose, stay quiet")]
    public void Warns_only_when_there_is_trie_data_to_lose(bool hasTrieData)
    {
        // TestLogger flattens every level into one list, so the level itself needs a substitute.
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.IsDebug.Returns(true);

        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(Flags.Drop | Flags.Enabled | Flags.FlatHasData),
            () => Persistence(true),
            () => new OneLoggerLogManager(new ILogger(logger)),
            () => hasTrieData);

        Assert.That(dropped, Is.True, "the deletion is unconditional so that an interrupted one completes");
        logger.Received(hasTrieData ? 1 : 0).Warn(Arg.Is<string>(s => s.Contains("irreversible", StringComparison.OrdinalIgnoreCase)));
        logger.Received(hasTrieData ? 0 : 1).Debug(Arg.Any<string>());
    }

    [TestCase(Flags.Drop | Flags.Enabled, Description = "Flat store empty -> say why the trie is kept")]
    [TestCase(Flags.Drop | Flags.FlatHasData, Description = "Flat DB disabled -> say why the trie is kept")]
    public void Declining_is_an_info_line_not_a_warning(Flags flags)
    {
        // The flag is opt-in, so an operator who set it is owed the reason nothing happened.
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.IsInfo.Returns(true);

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => Persistence(flags.HasFlag(Flags.FlatHasData)),
            () => new OneLoggerLogManager(new ILogger(logger)),
            () => throw new AssertionException("the disk must not be touched when declining"));

        logger.DidNotReceive().Warn(Arg.Any<string>());
        logger.Received(1).Info(Arg.Any<string>());
    }

    [Test]
    public void State_db_resolves_with_the_flat_gate_in_its_factory()
    {
        // The state DB factory resolves the flat IPersistence to decide, so a real container is the only
        // place a dependency cycle would show up. TestNethermindModule's flat store is empty, so the gate
        // declines here and nothing is dropped.
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
        // A MemDb ignores DeleteOnStart, so the settings handed to the factory are the only evidence.
        using TempPath tempPath = TempPath.GetTempDirectory();
        DbSettings stateDbSettings = null;
        IDbFactory dbFactory = DbFactory(tempPath.Path);
        dbFactory.CreateDb(Arg.Do<DbSettings>(settings =>
        {
            // FullPruningInnerDbFactory clones the settings as State or State{index}, hence the prefix.
            if (settings.DbName.StartsWith(nameof(DbNames.State), StringComparison.Ordinal)) stateDbSettings = settings;
        })).Returns(new MemDb());

        using IContainer container = Container(flags, dbFactory);

        container.ResolveKeyed<IDb>(DbNames.State);

        Assert.That(stateDbSettings?.DeleteOnStart, Is.EqualTo(expected));
    }

    [Test]
    public void Dropping_reclaims_the_copy_left_by_an_interrupted_full_pruning([Values] bool drop)
    {
        // Full pruning copies state/0 into state/1 and clears the loser when it finishes; interrupted, it leaves
        // both. Only the next pruning would clear the copy, and a flat node never runs one.
        using TempPath tempPath = TempPath.GetTempDirectory();
        string statePath = Path.Combine(tempPath.Path, DbNames.State);
        string live = Path.Combine(statePath, "0");
        string leftover = Path.Combine(statePath, "1");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(leftover);
        File.WriteAllBytes(Path.Combine(live, "000001.sst"), [1]);
        File.WriteAllBytes(Path.Combine(leftover, "000002.sst"), [1]);
        Flags flags = Flags.Enabled | Flags.FlatHasData | (drop ? Flags.Drop : Flags.None);

        using IContainer container = Container(flags, DbFactory(tempPath.Path));

        container.ResolveKeyed<IDb>(DbNames.State);

        // The live inner DB is RocksDB's to delete through DeleteOnStart, which the substituted factory never does.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.Exists(live), Is.True);
            Assert.That(Directory.Exists(leftover), Is.EqualTo(!drop));
        }
    }

    private static IContainer Container(Flags flags, IDbFactory dbFactory) => new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .Intercept<IFlatDbConfig>((cfg) =>
        {
            cfg.Enabled = flags.HasFlag(Flags.Enabled);
            cfg.DropPruningTrieState = flags.HasFlag(Flags.Drop);
        })
        .AddSingleton<IPersistence>(Persistence(flags.HasFlag(Flags.FlatHasData)))
        .AddSingleton<IDbFactory>(dbFactory)
        .Build();

    /// <summary>A factory that hands out MemDbs but resolves paths under <paramref name="basePath"/>, so the
    /// module's file-system checks run against a real directory instead of the working directory.</summary>
    private static IDbFactory DbFactory(string basePath)
    {
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(new MemDb());
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(ci => Path.Combine(basePath, ci.Arg<DbSettings>().DbPath));
        return dbFactory;
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
