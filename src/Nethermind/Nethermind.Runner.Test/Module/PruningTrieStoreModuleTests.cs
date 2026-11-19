// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.FullPruning;
using Nethermind.JsonRpc.Modules.Admin;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
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
        ImportFromPruningTrieState = 8,
        Repaired = 16,
        WipedForSync = 32,
        Migrating = 64
    }

    [TestCase(Flags.None, false, Description = "Flag off -> keep")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, false, Description = "Flag off even with flat in charge -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData, true, Description = "Flat owns a populated store -> drop")]
    [TestCase(Flags.Drop | Flags.FlatHasData, false, Description = "Flat DB disabled, node runs on patricia -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled, false, Description = "Flat store still empty -> keep, or the node loses all state")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.ImportFromPruningTrieState, true, Description = "Import finished and left configured -> drop; the fallback boundary stops reading the trie once flat is populated")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.Repaired, false, Description = "Repaired flat may be wiped on this start -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.WipedForSync, false, Description = "Interrupted wipe is redone on this start -> keep")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.Migrating, false, Description = "Flat still reads what its migration has not copied -> keep")]
    public void Drops_only_when_flat_owns_a_populated_store(Flags flags, bool expected)
    {
        bool dropped = PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => FlatDb(flags),
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
            () => throw new AssertionException("the flat DB must not be resolved"),
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
            () => FlatDb(Flags.FlatHasData),
            () => new OneLoggerLogManager(new ILogger(logger)),
            () => hasTrieData);

        Assert.That(dropped, Is.True, "the deletion is unconditional so that an interrupted one completes");
        logger.Received(hasTrieData ? 1 : 0).Warn(Arg.Is<string>(s => s.Contains("irreversible", StringComparison.OrdinalIgnoreCase)));
        logger.Received(hasTrieData ? 0 : 1).Debug(Arg.Any<string>());
    }

    [TestCase(Flags.Drop | Flags.Enabled, Description = "Flat store empty -> say why the trie is kept")]
    [TestCase(Flags.Drop | Flags.FlatHasData, Description = "Flat DB disabled -> say why the trie is kept")]
    [TestCase(Flags.Drop | Flags.Enabled | Flags.FlatHasData | Flags.Repaired, Description = "Flat DB repaired -> say why the trie is kept")]
    public void Declining_is_an_info_line_not_a_warning(Flags flags)
    {
        // The flag is opt-in, so an operator who set it is owed the reason nothing happened.
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.IsInfo.Returns(true);

        PruningTrieStoreModule.ShouldDropPruningTrieState(
            Config(flags),
            () => FlatDb(flags),
            () => new OneLoggerLogManager(new ILogger(logger)),
            () => throw new AssertionException("the disk must not be touched when declining"));

        logger.DidNotReceive().Warn(Arg.Any<string>());
        logger.Received(1).Info(Arg.Any<string>());
    }

    [Test]
    public async Task Pruning_trigger_defers_during_state_sync_and_starts_after_completion(
        [Values(SyncMode.StateNodes, SyncMode.StateNodes | SyncMode.FastHeaders)] SyncMode syncing)
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        selector.Current.Returns(syncing);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogManager logs = new OneLoggerLogManager(new ILogger(logger));
        const string refusal = "Full pruning is delayed while state synchronization is running.";
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IDbFactory>(new FreshPathMemDbFactory(tempPath.Path))
            .AddSingleton(selector)
            .AddSingleton(logs)
            .Intercept<IFlatDbConfig>(config => config.Enabled = false)
            .Intercept<IPruningConfig>(config =>
            {
                config.Mode = PruningMode.Full;
                config.AvailableSpaceCheckEnabled = false;
            })
            .Build();
        IPruningTrieStateAdminRpcModule admin = container.Resolve<IPruningTrieStateAdminRpcModule>();
        FullPruningDb db = (FullPruningDb)container.ResolveKeyed<IDb>(DbNames.State);
        int starts = 0;
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        db.PruningStarted += (_, _) => starts++;
        db.PruningFinished += (_, _) => completed.TrySetResult();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(admin.admin_prune().Data, Is.EqualTo(PruningStatus.Delayed));
                Assert.That(starts, Is.Zero);
                Assert.That(db.CanStartPruning, Is.True);
            }
            logger.Received(1).Info(refusal);
            selector.Current.Returns(SyncMode.Full);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(admin.admin_prune().Data, Is.EqualTo(PruningStatus.Starting));
                Assert.That(starts, Is.EqualTo(1));
            }
            logger.Received(1).Info(refusal);
        }
        finally
        {
            container.Resolve<IProcessExitSource>().Exit(ExitCodes.Ok);
            if (starts != 0)
            {
                using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(10));
                await completed.Task.WaitAsync(watchdog.Token);
            }
        }
        Assert.That(db.CanStartPruning, Is.True);
    }

    [Test]
    public void Patricia_world_state_activation_defers_real_sync_selector_resolution()
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IDbFactory>(new FreshPathMemDbFactory(tempPath.Path))
            .Intercept<IFlatDbConfig>(config => config.Enabled = false)
            .Intercept<IPruningConfig>(config => config.Mode = PruningMode.Full)
            .Build();
        IWorldStateManager worldState = container.Resolve<IWorldStateManager>();
        ISyncModeSelector selector = container.Resolve<ISyncModeSelector>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(worldState, Is.SameAs(container.Resolve<IWorldStateManager>()));
            Assert.That(selector, Is.SameAs(container.Resolve<ISyncModeSelector>()));
        }
    }

    [Test]
    public void Diagnostic_state_supports_atomic_abort_and_patricia_retry_through_production_modules()
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IDbFactory>(new FreshPathMemDbFactory(tempPath.Path))
            .Intercept<IFlatDbConfig>(config => config.Enabled = false)
            .Build();
        IDb state = container.ResolveKeyed<IDb>(DbNames.State);
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        Assert.That(factory, Is.InstanceOf<PatriciaSnapTrieFactory>());
        AssertAtomicAbortAndRetry(container, state, factory);
        FullPruningDb pruningDb = (FullPruningDb)state;
        Assert.That(pruningDb.TryStartPruning(out IPruningContext pruning), Is.True);
        using (pruning)
        {
            pruning.Set([255], [43]);
            pruning.Set([254], [1]);
            pruning.Commit();
        }
        AssertAtomicAbortAndRetry(container, state, factory);
    }

    [TestCase("State", true)]
    [TestCase("sTaTe0", true)]
    [TestCase("State1", true)]
    [TestCase("state2147483647", true)]
    [TestCase("StateFoo", false)]
    [TestCase("State-1", false)]
    [TestCase("State+1", false)]
    [TestCase("State1x", false)]
    [TestCase("blocks", false)]
    public void Diagnostic_factory_limits_atomic_capability_to_state_databases(string name, bool atomic)
    {
        using IDb db = new MemDbFactory().CreateDb(new DbSettings(name, name));
        Assert.That(db, Is.TypeOf(atomic ? typeof(SnapshotableMemDb) : typeof(MemDb)));
    }

    private static void AssertAtomicAbortAndRetry(IContainer container, IDb state, ISnapTrieFactory factory)
    {
        byte[] seedKey = [255];
        byte[] pendingKey = [254];
        state.Set(seedKey, [42]);
        state.Set(pendingKey, null);
        IAtomicWriteBatch acquired = null;
        Assert.That(() => acquired = state.StartAtomicWriteBatch(), Throws.Nothing);
        using (IAtomicWriteBatch aborted = acquired!)
        {
            aborted.Set(seedKey, [43]);
            aborted.Set(pendingKey, [1]);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Get(seedKey), Is.EqualTo(new byte[] { 42 }));
            Assert.That(state.Get(pendingKey), Is.Null);
        }
        using (IAtomicWriteBatch retry = state.StartAtomicWriteBatch())
        {
            retry.Set(seedKey, [43]);
            retry.Set(pendingKey, [1]);
            retry.Commit();
        }
        Hash256 address = Keccak.Compute("diagnostic-storage-account");
        Hash256 root;
        using (ISnapTree<PathWithStorageSlot> storage = factory.CreateStorageTree(address.ValueHash256))
        {
            storage.BulkSetAndUpdateRootHash([new PathWithStorageSlot(ValueKeccak.Zero, [1])]);
            root = storage.RootHash;
            storage.Commit(ValueKeccak.Zero, ValueKeccak.MaxValue);
        }
        byte[] persisted = container.Resolve<INodeStorage>().Get(address, TreePath.Empty, root.ValueHash256);
        Assert.That(persisted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Keccak.Compute(persisted!), Is.EqualTo(root));
            Assert.That(state.Get(seedKey), Is.EqualTo(new byte[] { 43 }));
            Assert.That(state.Get(pendingKey), Is.EqualTo(new byte[] { 1 }));
        }
    }

    [Test]
    public void State_db_resolves_with_the_flat_gate_in_its_factory()
    {
        // The state DB factory resolves the flat columns DB to decide, so a real container is the only
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

    private sealed class FreshPathMemDbFactory(string basePath) : IDbFactory
    {
        private readonly MemDbFactory _factory = new();

        public IDb CreateDb(DbSettings settings) => _factory.CreateDb(settings);
        public IColumnsDb<T> CreateColumnsDb<T>(string name) where T : struct, Enum => _factory.CreateColumnsDb<T>(name);
        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings settings) where T : struct, Enum => _factory.CreateColumnsDb<T>(settings);
        public string GetFullDbPath(DbSettings settings) => Path.Combine(basePath, settings.DbPath);
    }

    private static IContainer Container(Flags flags, IDbFactory dbFactory) => new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .Intercept<IFlatDbConfig>((cfg) =>
        {
            cfg.Enabled = flags.HasFlag(Flags.Enabled);
            cfg.DropPruningTrieState = flags.HasFlag(Flags.Drop);
        })
        .AddSingleton<IColumnsDb<FlatDbColumns>>(FlatDb(flags))
        // Constructed before the activation policy wipes a repaired flat DB, it would latch the pre-wipe slot encoding.
        .AddSingleton<IPersistence>(static _ => throw new AssertionException("opening the state DB must not construct the flat persistence"))
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

    private static FlatStateActivationPolicyTests.SpyFlatColumnsDb FlatDb(Flags flags)
    {
        FlatStateActivationPolicyTests.SpyFlatColumnsDb flatDb = new() { WasRepairedOnOpen = flags.HasFlag(Flags.Repaired) };
        if (flags.HasFlag(Flags.FlatHasData))
            new RocksDbPersistence(flatDb, LimboLogs.Instance, NullTrieNodeLog.Instance).CreateWriteBatch(StateId.PreGenesis, new StateId(1, Keccak.Zero), WriteFlags.None).Dispose();
        if (flags.HasFlag(Flags.Migrating))
            BasePersistence.WriteMigrationCursor(flatDb.GetColumnDb(FlatDbColumns.Metadata), MigrationCursor.Start);
        if (flags.HasFlag(Flags.WipedForSync))
            FlatStateActivationPolicyTests.MarkWipedForSync(flatDb);
        return flatDb;
    }
}
