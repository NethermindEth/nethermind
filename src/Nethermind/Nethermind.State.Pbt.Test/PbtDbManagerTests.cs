// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using Snapshot = Nethermind.State.Flat.Snapshot;
using System.Runtime.CompilerServices;
using Nethermind.Core.Test.Modules;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Init.Modules;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Api;
using Autofac;
using Nethermind.Core.Memory;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Migration;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Monitoring.Config;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using Nethermind.Api.Steps;

namespace Nethermind.State.Pbt.Test;

public class PbtDbManagerTests
{
    [Test]
    public async Task Retained_conversion_reopens_and_disabled_mode_leaves_files_untouched()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-" + Guid.NewGuid().ToString("N"));
        using MemDb catalogDb = new();
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        try
        {
            await using (IContainer container = RetainedContainer(path, catalogDb))
            {
                container.Resolve<IPbtDbManager>();
                PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
                PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, state);
                snapshot.TryLease();
                repository.TryAdd(snapshot);
                using (snapshot)
                {
                    IPbtRetainedSnapshotLoader loader = container.Resolve<IPbtRetainedSnapshotLoader>();
                    Assert.That(loader.ConvertAndRegister(snapshot), Is.True);
                    Assert.That(loader.ConvertAndRegister(snapshot), Is.True);
                    Assert.That(repository.Count, Is.EqualTo(1));
                    using (PbtReadOnlySnapshotBundle held = new(repository.TryLeaseReadChain(state, StateId.PreGenesis)!,
                        container.Resolve<IPbtPersistence>().CreateReader(), false))
                    {
                        repository.RemoveMemoryState(state);
                        Assert.That(held.GetCode(TestItem.KeccakC.ValueHash256)!.CodeSpan.Length, Is.EqualTo(65537));
                    }
                    using IPbtPersistence.IReader reader = container.Resolve<IPbtPersistence>().CreateReader();
                    Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
                }
            }
            string[] files = Directory.GetFiles(path, "*.bin", SearchOption.AllDirectories);
            byte[][] before = files.Select(File.ReadAllBytes).ToArray();
            await using (IContainer disabled = RetainedContainer(path, catalogDb, enabled: false))
            {
                disabled.Resolve<IPbtDbManager>();
                Assert.That(disabled.Resolve<PbtSnapshotRepository>().HasState(state), Is.False);
                Assert.That(disabled.Resolve<IPbtRetainedSnapshotLoader>(), Is.TypeOf<NullPbtRetainedSnapshotLoader>());
            }
            for (int i = 0; i < files.Length; i++) Assert.That(File.ReadAllBytes(files[i]), Is.EqualTo(before[i]));
            await using (IContainer reopened = RetainedContainer(path, catalogDb))
            {
                reopened.Resolve<IPbtDbManager>();
                PbtSnapshotRepository repository = reopened.Resolve<PbtSnapshotRepository>();
                Assert.That(repository.HasState(state), Is.True);
                using PbtSnapshotChain chain = repository.TryLeaseReadChain(state, StateId.PreGenesis)!;
                PbtRetainedSnapshot retained = chain.Layers[0].Retained!;
                Assert.That(retained.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
                Assert.That(retained.TryGetCode(TestItem.KeccakC.ValueHash256, out CodeInfo? code), Is.True);
                Assert.That(code!.CodeSpan.ToArray(), Is.EqualTo(new byte[65537]));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_publication_failure_preserves_source_and_recoverable_files([Values] bool rollbackFails)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-fault-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        LoaderFaultCatalog? fault = null;
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        try
        {
            await using (IContainer container = RetainedContainer(path, db, configure: builder =>
                builder.Register(ctx => fault = new LoaderFaultCatalog(new PbtSnapshotCatalog(db), rollbackFails))
                    .Keyed<ISnapshotCatalog>(DbNames.Pbt).SingleInstance()))
            {
                container.Resolve<IPbtDbManager>();
                PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
                PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, state);
                snapshot.TryLease(); repository.TryAdd(snapshot);
                using (snapshot)
                {
                    Assert.That(() => container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot),
                        Throws.InstanceOf<Exception>());
                    Assert.That(repository.Count, Is.EqualTo(1));
                    Assert.That(repository.RetainedCount, Is.Zero);
                }
                fault!.Fail = false;
            }
            await using IContainer reopened = RetainedContainer(path, db);
            reopened.Resolve<IPbtDbManager>();
            Assert.That(reopened.Resolve<PbtSnapshotRepository>().HasState(state), Is.EqualTo(rollbackFails));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_failed_startup_preserves_all_files([Values] bool badVersion)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-corrupt-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        try
        {
            await using (IContainer container = RetainedContainer(path, db))
            {
                container.Resolve<IPbtDbManager>();
                for (ulong i = 1; i <= 2; i++)
                {
                    StateId state = new(i, TestItem.KeccakA.ValueHash256);
                    PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, state);
                    snapshot.TryLease(); container.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
                    using (snapshot) Assert.That(container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot), Is.True);
                }
            }
            CatalogEntry entry = new PbtSnapshotCatalog(db).Load().First();
            string arenaPath = Directory.GetFiles(path, "*.bin", SearchOption.AllDirectories).First(f => Path.GetFileName(f).Contains("arena_"));
            using (FileStream stream = new(arenaPath, FileMode.Open, FileAccess.Write))
            {
                stream.Position = badVersion ? entry.Location.Offset + entry.Location.Size - 1 : entry.Location.Offset;
                stream.WriteByte(255); stream.Flush(true);
            }
            string[] files = Directory.GetFiles(path, "*.bin", SearchOption.AllDirectories);
            byte[][] bytes = files.Select(File.ReadAllBytes).ToArray();
            await using (IContainer failed = RetainedContainer(path, db))
            {
                Assert.That(() => failed.Resolve<IPbtDbManager>(), Throws.InstanceOf<Autofac.Core.DependencyResolutionException>());
                Assert.That(() => failed.Resolve<IPbtDbManager>(), Throws.InstanceOf<Autofac.Core.DependencyResolutionException>());
            }
            for (int i = 0; i < files.Length; i++) Assert.That(File.ReadAllBytes(files[i]), Is.EqualTo(bytes[i]));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_catalog_locations_are_checked_before_mapping([Values("negative", "overflow", "outside", "empty")] string fault)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-location-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        try
        {
            await using (IContainer initial = RetainedContainer(path, db))
            {
                initial.Resolve<IPbtDbManager>();
                PbtSnapshot snapshot = LoaderSnapshot(initial, StateId.PreGenesis, new(1, TestItem.KeccakA.ValueHash256));
                snapshot.TryLease(); initial.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
                using (snapshot) initial.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot);
            }
            PbtSnapshotCatalog catalog = new(db);
            CatalogEntry entry = catalog.Load().Single();
            SnapshotLocation location = fault switch
            {
                "negative" => new(entry.Location.ArenaId, -1, entry.Location.Size),
                "overflow" => new(entry.Location.ArenaId, long.MaxValue, entry.Location.Size),
                "outside" => new(entry.Location.ArenaId, 0, long.MaxValue),
                _ => new(entry.Location.ArenaId, entry.Location.Offset, 0),
            };
            catalog.Add(new(entry.From, entry.To, location, entry.Tier));
            string[] files = Directory.GetFiles(path, "*.bin", SearchOption.AllDirectories);
            byte[][] before = files.Select(File.ReadAllBytes).ToArray();
            await using (IContainer failed = RetainedContainer(path, db))
            {
                Assert.That(() => failed.Resolve<IPbtDbManager>(), Throws.InstanceOf<Autofac.Core.DependencyResolutionException>());
                Assert.That(() => failed.Resolve<IPbtDbManager>(), Throws.InstanceOf<Autofac.Core.DependencyResolutionException>());
            }
            for (int j = 0; j < files.Length; j++) Assert.That(File.ReadAllBytes(files[j]), Is.EqualTo(before[j]));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_build_failure_leaves_source_and_catalog_unchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-build-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        try
        {
            await using IContainer container = RetainedContainer(path, db);
            container.Resolve<IPbtDbManager>();
            PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, new(1, TestItem.KeccakA.ValueHash256));
            snapshot.Content.HeaderStorages[new Nethermind.Core.Collections.HashedKey<PbtPath>(default)] = SlotRun.Empty;
            snapshot.TryLease(); container.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
            using (snapshot)
            {
                Assert.That(() => container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot), Throws.InstanceOf<InvalidDataException>());
                Assert.That(container.Resolve<PbtSnapshotRepository>().ContainsMemorySource(snapshot), Is.True);
                Assert.That(new PbtSnapshotCatalog(db).Load(), Is.Empty);
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public void Retained_catalog_remove_retry_syncs_even_when_live_row_is_absent()
    {
        using LoaderSyncFailureDb db = new();
        PbtSnapshotCatalog catalog = new(db);
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        catalog.Add(new(StateId.PreGenesis, state, new(0, 0, 1), SnapshotTier.PersistedBase));
        db.FailNext = true;
        Assert.That(() => catalog.Remove(state, 2), Throws.InstanceOf<IOException>());
        int before = db.SyncCalls;
        Assert.That(catalog.Remove(state, 2), Is.False);
        Assert.That(db.SyncCalls, Is.EqualTo(before + 1));
    }

    [Test]
    public async Task Retained_catalog_durable_reopen_uses_production_database_factory()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-rocks-" + Guid.NewGuid().ToString("N"));
        PbtConfig config = new()
        {
            Enabled = true,
            CompactionOffset = 0,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        };
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                await using IContainer container = PbtTestContext.BuildProductionContainer(config,
                    builder => builder.AddSingleton<IDbFactory, Nethermind.Db.Rocks.RocksDbFactory>(),
                    new InitConfig { BaseDbPath = path });
                container.Resolve<IPbtDbManager>();
                PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
                if (pass == 0)
                {
                    PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, state);
                    snapshot.TryLease(); repository.TryAdd(snapshot);
                    using (snapshot) Assert.That(container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot), Is.True);
                }
                else Assert.That(repository.HasState(state), Is.True);
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_index_admission_failure_rolls_back_catalog()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-index-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        LoaderFaultCatalog? catalog = null;
        try
        {
            await using IContainer container = RetainedContainer(path, db, configure: builder =>
                builder.Register(_ => catalog = new LoaderFaultCatalog(new PbtSnapshotCatalog(db), false) { Fail = false })
                    .Keyed<ISnapshotCatalog>(DbNames.Pbt).SingleInstance());
            container.Resolve<IPbtDbManager>();
            PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
            PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, new(1, TestItem.KeccakA.ValueHash256));
            snapshot.TryLease(); repository.TryAdd(snapshot);
            catalog!.AfterAdd = repository.Dispose;
            using (snapshot)
            {
                Assert.That(() => container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot), Throws.InstanceOf<ObjectDisposedException>());
                Assert.That(new PbtSnapshotCatalog(db).Load(), Is.Empty);
                Assert.That(snapshot.Content.Codes[TestItem.KeccakC.ValueHash256].CodeSpan.Length, Is.EqualTo(65537));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_services_are_isolated_from_flat_in_the_same_container()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-loader-dual-" + Guid.NewGuid().ToString("N"));
        PbtConfig config = new()
        {
            Enabled = true,
            CompactionOffset = 0,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        };
        FlatDbConfig flat = new()
        {
            CompactionOffset = 0,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        };
        try
        {
            await using IContainer container = PbtTestContext.BuildProductionContainer(config,
                builder => builder.AddModule(new Nethermind.Init.Modules.FlatWorldStateModule(flat)),
                new InitConfig { BaseDbPath = path }, flat);
            container.Resolve<IPbtDbManager>();
            container.Resolve<IPersistedSnapshotLoader>().Load();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(container.ResolveKeyed<IArenaManager>(DbNames.Pbt), Is.Not.SameAs(container.Resolve<IArenaManager>()));
                Assert.That(container.ResolveKeyed<BlobArenaManager>(DbNames.Pbt), Is.Not.SameAs(container.Resolve<BlobArenaManager>()));
                Assert.That(container.ResolveKeyed<ISnapshotCatalog>(DbNames.Pbt), Is.Not.SameAs(container.Resolve<ISnapshotCatalog>()));
            }
            PbtSnapshot snapshot = LoaderSnapshot(container, StateId.PreGenesis, new(1, TestItem.KeccakA.ValueHash256));
            snapshot.TryLease(); container.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
            using (snapshot) container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot);
            Assert.That(container.Resolve<ISnapshotCatalog>().Load(), Is.Empty);
            Assert.That(container.ResolveKeyed<ISnapshotCatalog>(DbNames.Pbt).Load().Count(), Is.EqualTo(1));
            Assert.That(Directory.Exists(Path.Combine(path, DbNames.Pbt, "retained-snapshots-v1")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(path, "persistedSnapshot")), Is.True);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Retained_compactor_production_disposal_drains_and_preserves_catalogued_outputs([Values] bool enabled)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-compactor-reopen-" + Guid.NewGuid().ToString("N"));
        using MemDb catalog = new();
        PbtConfig config = new()
        {
            Enabled = true,
            EnableLongFinality = enabled,
            CompactSize = 4,
            CompactionOffset = 0,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        };
        IContainer Open() => PbtTestContext.BuildProductionContainer(config,
            builder => builder.RegisterInstance(catalog).Keyed<IDb>(PbtSnapshotCatalog.DatabaseKey).ExternallyOwned(),
            new InitConfig { BaseDbPath = path });
        try
        {
            await using (IContainer initial = Open())
            {
                initial.Resolve<IPbtRetainedSnapshotLoader>().Load();
                IPbtRetainedSnapshotCompactor compactor = initial.Resolve<IPbtRetainedSnapshotCompactor>();
                Assert.That(compactor, enabled ? Is.TypeOf<PbtRetainedSnapshotCompactor>() : Is.TypeOf<NullPbtRetainedSnapshotCompactor>());
                ArrayPoolList<StateId> batch = new(8);
                if (enabled)
                {
                    PbtSnapshotRepository repository = initial.Resolve<PbtSnapshotRepository>();
                    for (ulong block = 1; block <= 8; block++)
                    {
                        StateId state = new(block, TestItem.KeccakA.ValueHash256);
                        PbtSnapshot snapshot = LoaderSnapshot(initial, new(block - 1, TestItem.KeccakA.ValueHash256), state);
                        snapshot.TryLease();
                        repository.TryAdd(snapshot);
                        using (snapshot) initial.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot);
                        batch.Add(state);
                    }
                }
                await compactor.EnqueueAsync(batch, 0, CancellationToken.None);
                if (!enabled) Assert.That(() => batch.Add(StateId.PreGenesis), Throws.InstanceOf<ObjectDisposedException>());
            }
            if (!enabled)
            {
                Assert.That(Directory.Exists(Path.Combine(path, DbNames.Pbt, "retained-snapshots-v1")), Is.False);
                return;
            }
            await using IContainer reopened = Open();
            reopened.Resolve<IPbtRetainedSnapshotLoader>().Load();
            PbtSnapshotRepository loaded = reopened.Resolve<PbtSnapshotRepository>();
            StateId head = new(8, TestItem.KeccakA.ValueHash256);
            Assert.That(loaded.TryLeaseRetained(head, 4, SnapshotTier.PersistedCompactSized, out PbtRetainedSnapshot? compactSized), Is.True);
            using (compactSized) Assert.That(compactSized!.TryGetCode(TestItem.KeccakC.ValueHash256, out CodeInfo? code), Is.True);
            Assert.That(loaded.TryLeaseRetained(head, 8, SnapshotTier.PersistedLargeCompacted, out PbtRetainedSnapshot? large), Is.True);
            large?.Dispose();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private sealed class LoaderSyncFailureDb : MemDb
    {
        internal bool FailNext { get; set; }
        internal int SyncCalls { get; private set; }
        public override void Flush(bool onlyWal = false)
        {
            SyncCalls++;
            if (FailNext) { FailNext = false; throw new IOException("injected WAL sync failure"); }
        }
    }

    private static IContainer RetainedContainer(string path, IDb catalog, bool enabled = true, Action<ContainerBuilder>? configure = null) =>
        PbtTestContext.BuildProductionContainer(new PbtConfig
        {
            Enabled = true,
            EnableLongFinality = enabled,
            CompactionOffset = 0,
            ValidatePersistedSnapshot = true,
            ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576,
            PersistedSnapshotArenaPageCacheBytes = 0,
        }, builder =>
        {
            builder.RegisterInstance(catalog).Keyed<IDb>(PbtSnapshotCatalog.DatabaseKey).ExternallyOwned();
            configure?.Invoke(builder);
        }, new InitConfig { BaseDbPath = path });

    private static PbtSnapshot LoaderSnapshot(IContainer container, StateId from, StateId to)
    {
        IPbtResourcePool pool = container.Resolve<IPbtResourcePool>();
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        content.Codes[TestItem.KeccakC.ValueHash256] = new CodeInfo(new byte[65537]);
        ValueHash256 address = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        content.Accounts[address] = null;
        content.SelfDestructedStorageAddresses[address] = false;
        content.HeaderStorages[SlotRun.RunKey(PbtStateKey.HeaderStorage(address, 0))] = SlotRun.Empty;
        content.Storages[SlotRun.RunKey(PbtStateKey.Storage(TestItem.AddressA, address, 1000))] = SlotRun.Empty;
        content.AccountNodeGroups[default] = null;
        return new(from, to, TestItem.KeccakB.ValueHash256, content, pool, PbtResourcePool.Usage.MainBlockProcessing);
    }

    private sealed class LoaderFaultCatalog(ISnapshotCatalog inner, bool rollbackFails) : ISnapshotCatalog
    {
        internal bool Fail { get; set; } = true;
        internal Action? AfterAdd { get; set; }
        public void Add(CatalogEntry entry)
        {
            inner.Add(entry);
            AfterAdd?.Invoke();
            if (Fail) throw new IOException("injected catalog publication failure");
        }
        public bool Remove(in StateId to, long depth)
        {
            if (Fail && rollbackFails) throw new IOException("injected rollback failure");
            return inner.Remove(to, depth);
        }
        public IEnumerable<CatalogEntry> Load() => inner.Load();
    }

    [Test]
    public async Task Production_module_shares_trie_cache_and_reports_inactive_migration()
    {
        PbtConfig config = new() { Enabled = true };
        await using IContainer container = PbtTestContext.BuildProductionContainer(config);
        PbtTrieNodeCache cache = container.Resolve<PbtTrieNodeCache>();
        using ILifetimeScope child = container.BeginLifetimeScope();
        IMigrationDebugRpcModule rpcModule = container.Resolve<IMigrationDebugRpcModule>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.Resolve<PbtTrieNodeCache>(), Is.SameAs(cache));
            Assert.That(container.Resolve<IPbtDbManager>(), Is.TypeOf<PbtDbManager>());
            Assert.That(config.AccountTrieNodeCacheSizeBudget, Is.EqualTo(134217728UL));
            Assert.That(config.CodeTrieNodeCacheSizeBudget, Is.EqualTo(33554432UL));
            Assert.That(config.StorageTrieNodeCacheSizeBudget, Is.EqualTo(234881024UL));
            Assert.That(container.Resolve<IReadOnlyList<RpcModuleInfo>>().Select(static m => m.ModuleType), Has.Member(typeof(IMigrationDebugRpcModule)));
            Assert.That(rpcModule.debug_migrationProgress().Data, Is.EqualTo(MigrationProgressForRpc.Inactive));
            Assert.That(rpcModule.debug_shadowStateRoot(TestItem.KeccakA).Data, Is.Null);
        }
    }

    [Test]
    public async Task Command_steps_are_registered_and_selected_by_config([Values] bool import, [Values] bool scan)
    {
        PbtConfig config = new() { Enabled = true, ImportFromPreimageFlat = import, ScanTree = scan };
        await using IContainer container = PbtTestContext.BuildProductionContainer(config);

        List<Type> expectedTargets = [];
        if (import) expectedTargets.Add(typeof(ImportPbtFromPreimageFlat));
        if (scan) expectedTargets.Add(typeof(ScanPbtTree));
        StepInfo[] steps = [.. container.Resolve<IEnumerable<StepInfo>>()];
        IEnumerable<string?> commands = steps.Select(static step => step.Command);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(commands, Does.Contain("import-pbt"));
            Assert.That(commands, Does.Contain("scan-pbt"));
            Assert.That(container.Resolve<IEnumerable<StepTarget>>().Select(static target => target.StepBaseType), Is.EquivalentTo(expectedTargets));
        }
    }

    [Test]
    public async Task Production_module_registers_carry_forward_decorator_only_when_enabled([Values] bool carryForwardCache)
    {
        PbtConfig config = new() { Enabled = true, CarryForwardCache = carryForwardCache };
        await using IContainer container = PbtTestContext.BuildProductionContainer(config);

        Assert.That(container.Resolve<IPbtPersistence>(), carryForwardCache ? Is.TypeOf<PbtCarryForwardCachingPersistence>() : Is.TypeOf<PbtCachedReaderPersistence>());
    }

    private static readonly Address Address = TestItem.AddressA;

    /// <summary>The per-block storage slot, on a stem separate from the account header.</summary>
    private static readonly UInt256 Slot = 1000;

    private static BlockHeader Header(ulong number, Hash256 root) => Build.A.BlockHeader.WithNumber(number).WithStateRoot(root).TestObject;

    [Test]
    public async Task ReadOnlyBundle_IsSharedPerState_UntilTheBoundarySweepReleasesIt()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null))
        {
            root1 = scope.CommitBlock(1, Address, 100, Slot);
        }

        StateId state = new(1, root1);
        IPbtDbManager manager = ctx.Manager;
        PbtReadOnlySnapshotBundle first = manager.GatherReadOnlyBundle(state);
        PbtReadOnlySnapshotBundle second = manager.GatherReadOnlyBundle(state);
        Assert.That(second, Is.SameAs(first), "one state, one shared view");

        // The gather retains its leased view after the sweep drops the cached view.
        ctx.Manager.FlushCache(default);
        Assert.That(first.GetAccount(Address)!.Value.ToAccount().Balance, Is.EqualTo((UInt256)100));

        PbtReadOnlySnapshotBundle afterSweep = manager.GatherReadOnlyBundle(state);
        Assert.That(afterSweep, Is.Not.SameAs(first), "the swept view is not handed out again");

        first.Dispose();
        second.Dispose();
        afterSweep.Dispose();
    }

    [Test]
    public async Task CommitFlushReopen_ServesPersistedState_AndPrunesHistory()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        Hash256 root1;
        Hash256 root3;

        await using (PbtTestContext ctx = new(db))
        {
            using (IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null))
            {
                root1 = scope.CommitBlock(1, Address, 100, Slot);
                scope.CommitBlock(2, Address, 200, Slot);
                root3 = scope.CommitBlock(3, Address, 300, Slot);
            }

            ctx.Manager.FlushCache(default);
            using Persistence.IPbtPersistence.IReader reader = ctx.Persistence.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(3, root3)));
        }

        await using (PbtTestContext reopened = new(db))
        {
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(3, root3)), Is.True);
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(1, root1)), Is.False);
            Assert.That(reopened.Manager.TryGatherReadOnlyBundle(new StateId(1, root1)), Is.Null);

            using IWorldStateScopeProvider.IScope scope = reopened.BeginScope(Header(3, root3));
            Account? account = scope.Get(Address);
            Assert.That(account, Is.Not.Null);
            Assert.That(account!.Nonce, Is.EqualTo(3ul));
            Assert.That(account.Balance, Is.EqualTo((UInt256)300));
            Assert.That(scope.CreateStorageTree(Address).Get(Slot), Is.EqualTo((UInt256)3), "and the slot decodes out of its own persisted blob");
        }
    }

    [Test]
    public async Task ForkCommitsFromSameParent_BothStatesReadable()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null))
        {
            root1 = scope.CommitBlock(1, Address, 100, Slot);
        }

        Hash256 rootA;
        Hash256 rootB;
        using (IWorldStateScopeProvider.IScope scopeA = ctx.BeginScope(Header(1, root1)))
        {
            rootA = scopeA.CommitBlock(2, Address, 222, Slot);
        }

        using (IWorldStateScopeProvider.IScope scopeB = ctx.BeginScope(Header(1, root1)))
        {
            rootB = scopeB.CommitBlock(2, Address, 333, Slot);
        }

        Assert.That(rootA, Is.Not.EqualTo(rootB));
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootA)), Is.True);
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootB)), Is.True);

        using (IWorldStateScopeProvider.IScope onA = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootA), new LocalMetrics()))
        {
            Assert.That(onA.Get(Address)!.Balance, Is.EqualTo((UInt256)222));
        }

        using (IWorldStateScopeProvider.IScope onB = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootB), new LocalMetrics()))
        {
            Assert.That(onB.Get(Address)!.Balance, Is.EqualTo((UInt256)333));
        }

        using IWorldStateScopeProvider.IScope onParent = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(1, root1), new LocalMetrics());
        Assert.That(onParent.Get(Address)!.Balance, Is.EqualTo((UInt256)100));
    }

    [Test]
    public async Task FinalizedTrigger_PersistsCanonicalSegments_AndPrunesRepository()
    {
        await using PbtTestContext ctx = new(config: new PbtConfig { CompactSize = 2, MinReorgDepth = 1, MaxReorgDepth = 100 });

        Hash256[] roots = new Hash256[6];
        using IWorldStateScopeProvider.IScope scope = ctx.BeginScope(null);
        for (ulong number = 1; number <= 5; number++)
        {
            roots[number] = scope.CommitBlock(number, Address, number * 100, Slot);
            ctx.FinalizedStateProvider.SetCanonicalRoot(number, roots[number]);
        }

        ctx.FinalizedStateProvider.FinalizedBlockNumber = 5;
        ctx.Coordinator.CheckPersistence(ctx.Repository.GetLastCommittedStateId()!.Value);

        // With CompactSize 2 and no offset, only even finalized blocks are persisted.
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(new StateId(4, roots[4])));
        Assert.That(ctx.Repository.Count, Is.EqualTo(1));

        // The open scope continues reading through its leased layers.
        Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)500));
    }

    /// <summary>
    /// The block's header already claims a root, so that is what the scope must report and key its
    /// state by; the root the tree folds to is kept beside it, on the sealed layer, for the next fold.
    /// Committing must also carry the resolved header forward, or the block after it in the same
    /// branch would resolve the child of the block just committed — itself. Persisted state is restored
    /// by the header root while continuing from the persisted tree root.
    /// </summary>
    [Test]
    public async Task PersistedState_IsKeyedByTheHeaderRoot_WithTheTreeRootRecordedBesideIt()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtTestChildHeaders childHeaders = new();
        BlockHeader second;
        ValueHash256 treeRoot;

        await using (PbtTestContext ctx = new(db, childHeaders: childHeaders))
        {
            // Block 0 has no header root, so genesis claims its tree root.
            BlockHeader genesis;
            using (IWorldStateScopeProvider.IScope genesisScope = ctx.BeginScope(null))
            {
                genesis = Header(0, genesisScope.CommitBlock(0, Address, 1, Slot));
            }

            BlockHeader first = childHeaders.Add(genesis, TestItem.KeccakA);
            second = childHeaders.Add(first, TestItem.KeccakB);
            using (IWorldStateScopeProvider.IScope scope = ctx.BeginScope(genesis))
            {
                Assert.That(scope.CommitBlock(1, Address, 100, Slot), Is.EqualTo(TestItem.KeccakA), "the block reports the root its header claims");
                Assert.That(scope.CommitBlock(2, Address, 200, Slot), Is.EqualTo(TestItem.KeccakB), "and the next block in the branch resolves its own header");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Manager.HasStateForBlock(new StateId(first)), Is.True, "both states are keyed by their header");
                Assert.That(ctx.Manager.HasStateForBlock(new StateId(second)), Is.True);
            }

            using (PbtSnapshotBundle bundle = ((IPbtDbManager)ctx.Manager).GatherBundle(new StateId(second), PbtResourcePool.Usage.ReadOnlyProcessingEnv))
            {
                treeRoot = bundle.TreeRoot;
                Assert.That(bundle.GetAccount(Address)!.Balance, Is.EqualTo((UInt256)200), "the state is readable through the header-keyed id");
            }

            Assert.That(treeRoot, Is.Not.EqualTo(TestItem.KeccakB.ValueHash256), "which is not the root the tree folded to");
            ctx.Manager.FlushCache(default);
        }

        await using (PbtTestContext reopened = new(db, childHeaders: childHeaders))
        {
            using (Persistence.IPbtPersistence.IReader reader = reopened.Persistence.CreateReader())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(new StateId(second)));
                Assert.That(reader.CurrentRoot, Is.EqualTo(treeRoot));
            }

            BlockHeader third = childHeaders.Add(second, TestItem.KeccakC);
            using IWorldStateScopeProvider.IScope scope = reopened.BeginScope(second);
            Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)200), "the persisted state is found by its header");
            Assert.That(scope.CommitBlock(3, Address, 300, Slot), Is.EqualTo(third.StateRoot), "and the branch carries on from it");
        }
    }

    [Test]
    public void Persistence_PrefersExistingUnits_AndBoundsBackgroundDrain([Values(32, 1)] int width, [Values] PersistTrigger trigger)
    {
        using CoordinatorHarness harness = new(new PbtConfig { EnableLongFinality = false, CompactSize = 32, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 32 });
        List<(StateId From, StateId To)> writes = [];
        harness.Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Returns(call =>
            {
                StateId from = call.ArgAt<StateId>(0);
                StateId to = call.ArgAt<StateId>(1);
                Assert.That(call.ArgAt<ValueHash256>(2), Is.EqualTo(to.StateRoot));
                writes.Add((from, to));
                return Substitute.For<IPbtPersistence.IWriteBatch>();
            });
        for (int number = 1; number <= 192; number++)
        {
            harness.Repository.TryAdd(PersistenceSnapshot(number - 1, number, harness.Pool));
            harness.Finalized.SetCanonicalRoot((ulong)number, TestItem.KeccakA);
            if (width > 1 && number % width == 0)
                harness.Repository.TryAddCompacted(PersistenceSnapshot(number - width, number, harness.Pool));
        }
        if (trigger == PersistTrigger.FinalizedCheck) harness.Finalized.FinalizedBlockNumber = 192;
        if (trigger is PersistTrigger.Check or PersistTrigger.FinalizedCheck) harness.Coordinator.CheckPersistence(PersistenceState(192));
        else harness.Coordinator.FlushToPersistence(CancellationToken.None);

        Assert.That(writes.Count, Is.EqualTo(trigger is PersistTrigger.Check or PersistTrigger.FinalizedCheck ? 4 : 192 / width));
        for (int index = 0; index < writes.Count; index++)
            Assert.That(writes[index], Is.EqualTo((PersistenceState(index * width), PersistenceState((index + 1) * width))));
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(writes[^1].To));
    }

    /// <summary>The finalized trigger folds to the next boundary only while <c>MinReorgDepth</c> blocks stay above it.</summary>
    [TestCase(7, 0)]
    [TestCase(8, 4)]
    public void FinalizedTrigger_KeepsMinReorgDepthAboveTheNewBase(int head, int expectedPersisted)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 4, CompactionOffset = 0, MinReorgDepth = 4, MaxReorgDepth = 100 });
        for (int number = 1; number <= head; number++)
        {
            harness.Repository.TryAdd(PersistenceSnapshot(number - 1, number, harness.Pool));
            harness.Finalized.SetCanonicalRoot((ulong)number, TestItem.KeccakA);
        }
        harness.Repository.TryAddCompacted(PersistenceSnapshot(0, 4, harness.Pool));
        harness.Finalized.FinalizedBlockNumber = (ulong)head;

        harness.Coordinator.CheckPersistence(PersistenceState(head));

        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(expectedPersisted)));
    }

    [Test]
    public void Persistence_FailedCommitDoesNotPublishOrPrune()
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 2, CompactionOffset = 0 });
        bool failCommit = true;
        harness.Batch.When(value => value.Commit()).Do(_ =>
        {
            if (failCommit) throw new InvalidOperationException("Injected write failure");
        });
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 2, harness.Pool));
        Assert.Throws<InvalidOperationException>(() => harness.Coordinator.FlushToPersistence(CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
            Assert.That(harness.Repository.Count, Is.EqualTo(2));
        }
        harness.Batch.Received(1).Dispose();
        failCommit = false;
        harness.Coordinator.FlushToPersistence(CancellationToken.None);
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(2)));
    }

    [Test]
    public async Task PersistenceBackpressure_StallsProducer_AndShutdownDrains([Values] bool cancelProducer)
    {
        using CoordinatorHarness harness = new(new PbtConfig { EnableLongFinality = false, CompactSize = 1, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 1 });
        PbtResourcePool pool = harness.Pool;
        PbtSnapshotRepository repository = harness.Repository;
        using CancellationTokenSource processExit = new();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(processExit.Token);
        TaskCompletionSource enteredPersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource producerStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<StateId> persisted = [];
        harness.Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Returns(call =>
            {
                enteredPersistence.TrySetResult();
                releasePersistence.Task.GetAwaiter().GetResult();
                persisted.Add(call.ArgAt<StateId>(1));
                return Substitute.For<IPbtPersistence.IWriteBatch>();
            });
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.When(value => value.Warn(Arg.Any<string>())).Do(_ =>
        {
            if (repository.GetLastCommittedStateId() == PersistenceState(68)) producerStalled.TrySetResult();
        });
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger wrappedLogger = new(logger);
        logs.GetClassLogger<PbtDbManager>().Returns(wrappedLogger);
        PbtDbManager manager = harness.CreateManager(exitSource, logs, NoopPbtTrieNodeCache.Instance);
        Task producer = Task.CompletedTask;
        try
        {
            manager.AddSnapshot(PersistenceSnapshot(0, 1, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            manager.AddSnapshot(PersistenceSnapshot(1, 2, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            await enteredPersistence.Task.WaitAsync(TimeSpan.FromSeconds(10));
            producer = Task.Run(() =>
            {
                for (int number = 3; number <= 68; number++)
                    manager.AddSnapshot(PersistenceSnapshot(number - 1, number, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            });
            await producerStalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(producer.IsCompleted, Is.False);
            if (cancelProducer)
            {
                processExit.Cancel();
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (cancelProducer)
            {
                Task disposal = manager.DisposeAsync().AsTask();
                Assert.That(disposal.IsCompleted, Is.False, "persistence must finish before shutdown completes");
                releasePersistence.TrySetResult();
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                releasePersistence.TrySetResult();
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
                await manager.DisposeAsync();
            }
            await manager.DisposeAsync();
            // Shutdown drains the queued jobs but leaves the backstop's reorg depth in memory; a cancelled producer never queued 68.
            int expectedPersisted = cancelProducer ? 66 : 67;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(expectedPersisted)));
                Assert.That(persisted.Count, Is.EqualTo(expectedPersisted));
                Assert.That(repository.Count, Is.Zero);
            }
            for (int index = 0; index < persisted.Count; index++)
                Assert.That(persisted[index], Is.EqualTo(PersistenceState(index + 1)));
        }
        finally
        {
            releasePersistence.TrySetResult();
            await producer.WaitAsync(TimeSpan.FromSeconds(10));
            await manager.DisposeAsync();
        }
    }

    [Test]
    public async Task Disposal_DoesNotPersistTheUnfinalizedTail()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        Hash256 root;
        await using (PbtTestContext context = new(db, new PbtConfig()))
        {
            using (IWorldStateScopeProvider.IScope scope = context.BeginScope(null))
                root = scope.CommitBlock(1, Address, 100, Slot);
            await context.Manager.DisposeAsync();
            await context.Manager.DisposeAsync();
        }
        await using PbtTestContext reopened = new(db, new PbtConfig());
        using IPbtPersistence.IReader reader = reopened.Persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(StateId.PreGenesis));
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(1, root)), Is.False);
        }
    }

    public enum PersistTrigger { Check, Flush, FinalizedCheck }

    public enum TransientHandOff { Admitted, NoCache, Duplicate, ChannelFull }

    [Test]
    public async Task AddSnapshot_hands_the_transient_to_the_populator_or_releases_it([Values] TransientHandOff mode)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactionOffset = 0 });
        PbtResourcePool pool = harness.Pool;
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(CancellationToken.None);
        using PbtTrieNodeCache cache = new(harness.Config);
        // A closed gate parks the populator on the first transient, so the second fills the one-slot queue and the third finds it full.
        using ManualResetEventSlim populatorGate = new(mode != TransientHandOff.ChannelFull);
        TaskCompletionSource populatorEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPbtTrieNodeCache gatedCache = Substitute.For<IPbtTrieNodeCache>();
        gatedCache.When(c => c.Add(Arg.Any<PbtTransientResource>())).Do(call =>
        {
            populatorEntered.TrySetResult();
            populatorGate.Wait();
            cache.Add(call.Arg<PbtTransientResource>());
        });
        PbtDbManager manager = harness.CreateManager(exitSource, LimboLogs.Instance, mode == TransientHandOff.NoCache ? NoopPbtTrieNodeCache.Instance : gatedCache);
        ConcurrentBag<PbtTransientResource> returned = [];
        IPbtResourcePool recordingPool = Substitute.For<IPbtResourcePool>();
        recordingPool.When(p => p.ReturnCachedResource(Arg.Any<PbtResourcePool.Usage>(), Arg.Any<PbtTransientResource>())).Do(call =>
        {
            returned.Add(call.Arg<PbtTransientResource>());
            pool.ReturnCachedResource(call.Arg<PbtResourcePool.Usage>(), call.Arg<PbtTransientResource>());
        });
        PbtTransientResource first = StagedTransient(pool, recordingPool, 1);
        PbtTransientResource second = StagedTransient(pool, recordingPool, 2);
        PbtTransientResource third = StagedTransient(pool, recordingPool, 3);
        try
        {
            manager.AddSnapshot(PersistenceSnapshot(0, 1, pool), first);
            if (mode == TransientHandOff.ChannelFull) await populatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.AddSnapshot(PersistenceSnapshot(mode == TransientHandOff.Duplicate ? 0 : 1, mode == TransientHandOff.Duplicate ? 1 : 2, pool), second);
            if (mode == TransientHandOff.ChannelFull)
            {
                manager.AddSnapshot(PersistenceSnapshot(2, 3, pool), third);
                Assert.That(returned, Does.Contain(third), "a full cache queue releases the dropped transient immediately");
                populatorGate.Set();
            }
            else third.ReleaseLease();
            if (mode == TransientHandOff.Duplicate) Assert.That(returned, Does.Contain(second), "a transient that cannot reach the populator returns to the pool at once");
            Assert.That(() => cache.EntryCount, Is.EqualTo(mode switch { TransientHandOff.NoCache => 0, TransientHandOff.Duplicate => 1, TransientHandOff.Admitted => 2, _ => 2 }).After(5000, 10));
            Assert.That(() => returned, Is.EquivalentTo(new[] { first, second, third }).After(5000, 10), "every transient returns to the pool exactly once, ingested or refused");
            Assert.That(first.NodeGroups.Count + second.NodeGroups.Count + third.NodeGroups.Count, Is.Zero);
        }
        finally
        {
            populatorGate.Set();
            await manager.DisposeAsync();
        }
    }

    /// <summary>Rents a transient that stages one group and returns itself through <paramref name="returnPool"/>.</summary>
    private static PbtTransientResource StagedTransient(PbtResourcePool pool, IPbtResourcePool returnPool, byte marker)
    {
        PbtTransientResource transient = pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing);
        transient.OnRented(returnPool, PbtResourcePool.Usage.MainBlockProcessing);
        using RefCountingMemory payload = RefCountingMemory.OwningRocksDb(new ArrayMemoryManager([marker]));
        transient.NodeGroups.Set(new ValueHash256(TestItem.KeccakA.Bytes), new PbtNodePath([marker], 8), payload);
        return transient;
    }

    [Test]
    public async Task ResetPersistedStateId_IsNotUndoneByAReaderOpenedBeforeIt()
    {
        using CoordinatorHarness harness = new(new PbtConfig());
        IPbtPersistence.IReader beforeImport = Substitute.For<IPbtPersistence.IReader>();
        beforeImport.CurrentState.Returns(StateId.PreGenesis);
        IPbtPersistence.IReader afterImport = Substitute.For<IPbtPersistence.IReader>();
        afterImport.CurrentState.Returns(PersistenceState(2));
        using ManualResetEventSlim opened = new();
        using ManualResetEventSlim release = new();
        // The first reader is a snapshot taken before an anchor import wrote, held open across the import's reset.
        harness.Persistence.CreateReader().Returns(_ => { opened.Set(); release.Wait(); return beforeImport; }, _ => afterImport);
        PbtPersistenceCoordinator coordinator = harness.Coordinator;

        Task<StateId> racing = Task.Run(coordinator.GetCurrentPersistedStateId);
        opened.Wait();
        coordinator.ResetPersistedStateId();
        release.Set();

        Assert.That(await racing, Is.EqualTo(PersistenceState(2)));
        Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(2)));
    }

    private static StateId PersistenceState(int number) => new((ulong)number, TestItem.KeccakA.ValueHash256);

    /// <summary>A persistence coordinator over a mocked persistence whose reader starts at block 0 and whose write batches are <see cref="Batch"/>.</summary>
    [TestCase(false, 5, false)]
    [TestCase(false, 6, true)]
    [TestCase(true, 9, false)]
    [TestCase(true, 10, true)]
    public void Scheduler_selects_strict_mode_backstop(bool longFinality, int head, bool persists)
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            EnableLongFinality = longFinality,
            CompactSize = 2,
            CompactionOffset = 0,
            MinReorgDepth = 1,
            MaxReorgDepth = 5,
            LongFinalityMaxReorgDepth = 9,
        });
        for (int b = 1; b <= head; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        using PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(head));
        Assert.That(action.Persist is not null, Is.EqualTo(persists));
    }

    [Test]
    public void Scheduler_finalized_candidate_wins_and_missing_boundary_falls_back()
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            EnableLongFinality = false,
            CompactSize = 2,
            CompactionOffset = 0,
            MinReorgDepth = 0,
            MaxReorgDepth = 2,
        });
        for (int b = 1; b <= 6; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        harness.Repository.TryAddCompacted(PersistenceSnapshot(0, 2, harness.Pool));
        harness.Finalized.FinalizedBlockNumber = 6;
        harness.Finalized.SetCanonicalRoot(2, TestItem.KeccakA);
        using (PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(6)))
            Assert.That(action.Persist!.To, Is.EqualTo(PersistenceState(2)));
        // No boundary header exposes no canonical seed, but the independent backstop still acts.
        using CoordinatorHarness missing = new(harness.Config);
        for (int b = 1; b <= 6; b++) missing.Repository.TryAdd(PersistenceSnapshot(b - 1, b, missing.Pool));
        missing.Finalized.FinalizedBlockNumber = 6;
        using PbtPersistenceCoordinator.SnapshotAction fallback = missing.Coordinator.DetermineSnapshotAction(PersistenceState(6));
        Assert.That(fallback.Persist!.To, Is.EqualTo(PersistenceState(1)));
    }

    [Test]
    public void Scheduler_conversion_threshold_and_global_compacted_pass()
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            CompactSize = 4,
            CompactionOffset = 0,
            MinReorgDepth = 128,
            MaxInMemoryBaseSnapshotCount = 4,
        });
        for (int b = 1; b <= 4; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        harness.Repository.TryAddCompacted(PersistenceSnapshot(0, 4, harness.Pool));
        using (PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(4)))
            Assert.That(action.Convert, Is.Null, "threshold is strict and counts base snapshots");
        harness.Repository.TryAdd(PersistenceSnapshot(4, 5, harness.Pool));
        using PbtPersistenceCoordinator.SnapshotAction selected = harness.Coordinator.DetermineSnapshotAction(PersistenceState(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.ConvertRange, Is.True);
            Assert.That(selected.Convert!.To, Is.EqualTo(PersistenceState(4)), "global compacted pass beats earlier single base");
        }
    }

    [Test]
    public void Scheduler_rejects_known_finalized_fork_and_uses_committed_head()
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            EnableLongFinality = false,
            CompactSize = 2,
            CompactionOffset = 0,
            MinReorgDepth = 0,
            MaxReorgDepth = 2,
        });
        for (int b = 1; b <= 5; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        StateId fork = new(1, TestItem.KeccakB.ValueHash256);
        harness.Repository.TryAdd(new(PersistenceState(0), fork, TestItem.KeccakC.ValueHash256, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        harness.Repository.SetLastCommittedStateId(PersistenceState(5));
        using (PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(fork))
            Assert.That(action.Persist, Is.Null, "supplied height, rather than longest fork, controls depth");
        harness.Finalized.FinalizedBlockNumber = 1;
        harness.Finalized.SetCanonicalRoot(1, TestItem.KeccakB);
        using PbtPersistenceCoordinator.SnapshotAction rejected = harness.Coordinator.DetermineSnapshotAction(PersistenceState(5));
        Assert.That(rejected.Persist, Is.Null, "committed branch cannot persist a known nonfinalized root");
    }

    [TestCase(2, false)]
    [TestCase(3, true)]
    public void Scheduler_byte_pressure_preserves_minimum_depth_without_detailed_metrics(int head, bool persists)
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            CompactSize = 2,
            CompactionOffset = 0,
            MinReorgDepth = 2,
            MaxInMemorySnapshotBytes = 1,
        });
        for (int b = 1; b <= head; b++)
        {
            PbtSnapshot snapshot = PersistenceSnapshot(b - 1, b, harness.Pool);
            snapshot.Content.Codes[TestItem.KeccakC.ValueHash256] = new CodeInfo(new byte[100]);
            harness.Repository.TryAdd(snapshot);
        }
        using PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(head));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Repository.InMemorySnapshotBytes, Is.GreaterThan(1));
            Assert.That(action.Persist is not null, Is.EqualTo(persists));
        }
    }

    [Test]
    public async Task Scheduler_bulk_conversion_keeps_forks_and_never_advances_base()
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            CompactSize = 4,
            CompactionOffset = 0,
            MaxInMemoryBaseSnapshotCount = 0,
        });
        using PbtRetainedTestStore store = new();
        List<StateId> converted = [];
        SchedulerLoader loader = new(memory =>
        {
            using PbtRetainedSnapshot retained = store.Build(memory);
            harness.Repository.TryAddRetained(retained);
            converted.Add(memory.To);
            return true;
        });
        SchedulerCompactor compactor = new();
        using PbtPersistenceCoordinator coordinator = new(harness.Config, harness.Finalized, harness.Persistence, harness.Repository,
            harness.Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance, loader, compactor, CancellationToken.None);
        for (int b = 1; b <= 4; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        StateId fork1 = new(1, TestItem.KeccakB.ValueHash256), fork2 = new(2, TestItem.KeccakB.ValueHash256);
        harness.Repository.TryAdd(new(PersistenceState(0), fork1, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        harness.Repository.TryAdd(new(fork1, fork2, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        StateId orphan = new(3, TestItem.KeccakC.ValueHash256);
        harness.Repository.TryAdd(new(new StateId(2, TestItem.KeccakC.ValueHash256), orphan, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        harness.Repository.TryAddCompacted(PersistenceSnapshot(0, 4, harness.Pool));
        harness.Repository.SetLastCommittedStateId(PersistenceState(4));
        await coordinator.CheckPersistenceAsync(PersistenceState(4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted, Is.EquivalentTo(new[] { PersistenceState(1), PersistenceState(2), PersistenceState(3), PersistenceState(4), fork1, fork2 }));
            Assert.That(compactor.Enqueued, Is.EquivalentTo(converted));
            Assert.That(harness.Repository.GetInMemoryStates(), Is.EqualTo(new[] { orphan }));
            Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
        }
        harness.Repository.Dispose();
    }

    [Test]
    public async Task Scheduler_retained_restart_gather_and_flush_preserve_tree_root()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-scheduler-reopen-" + Guid.NewGuid().ToString("N"));
        using MemDb catalog = new();
        SnapshotableMemColumnsDb<PbtColumns> baseDb = new("pbt");
        StateId state = PersistenceState(1);
        Action<ContainerBuilder> configure = builder => builder.RegisterInstance(baseDb).As<IColumnsDb<PbtColumns>>().ExternallyOwned();
        try
        {
            await using (IContainer initial = RetainedContainer(path, catalog, configure: configure))
            {
                initial.Resolve<IPbtDbManager>();
                PbtSnapshot snapshot = LoaderSnapshot(initial, StateId.PreGenesis, state);
                snapshot.TryLease(); initial.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
                using (snapshot) initial.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot);
            }
            await using IContainer reopened = RetainedContainer(path, catalog, configure: configure);
            IPbtDbManager manager = reopened.Resolve<IPbtDbManager>();
            PbtPersistenceCoordinator coordinator = reopened.Resolve<PbtPersistenceCoordinator>();
            using PbtReadOnlySnapshotBundle held = manager.GatherReadOnlyBundle(state);
            Assert.That(held.GetCode(TestItem.KeccakC.ValueHash256)!.CodeSpan.Length, Is.EqualTo(65537));
            Assert.That(reopened.Resolve<PbtSnapshotRepository>().GetLastCommittedStateId(), Is.Null);
            manager.FlushCache(CancellationToken.None);
            using IPbtPersistence.IReader reader = reopened.Resolve<IPbtPersistence>().CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(state));
                Assert.That(reader.CurrentState, Is.EqualTo(state));
                Assert.That(reader.CurrentRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
                Assert.That(reader.GetCode(TestItem.KeccakC.ValueHash256)!.CodeSpan.Length, Is.EqualTo(65537));
                Assert.That(held.TreeRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256));
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Test]
    public async Task Gather_retries_with_fresh_reader_and_distinguishes_unknown_state()
    {
        using CoordinatorHarness harness = new(new PbtConfig());
        StateId state = PersistenceState(1);
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        IPbtPersistence.IReader stale = Substitute.For<IPbtPersistence.IReader>();
        stale.CurrentState.Returns(new StateId(0, TestItem.KeccakB.ValueHash256));
        IPbtPersistence.IReader fresh = Substitute.For<IPbtPersistence.IReader>();
        fresh.CurrentState.Returns(PersistenceState(0));
        harness.Persistence.CreateReader().Returns(stale, fresh);
        await using PbtDbManager manager = harness.CreateManager(new ProcessExitSource(CancellationToken.None), LimboLogs.Instance, NoopPbtTrieNodeCache.Instance);
        using PbtReadOnlySnapshotBundle bundle = manager.GatherReadOnlyBundle(state);
        stale.Received(1).Dispose();
        Assert.That(manager.TryGatherReadOnlyBundle(PersistenceState(9)), Is.Null);
        Assert.That(() => manager.GatherReadOnlyBundle(PersistenceState(9)), Throws.TypeOf<StateNotRetainedException>());
    }

    [Test]
    public void Scheduler_byte_pressure_does_not_drain_a_retained_candidate()
    {
        using CoordinatorHarness harness = new(new PbtConfig
        {
            CompactSize = 2,
            CompactionOffset = 0,
            MinReorgDepth = 1,
            MaxInMemorySnapshotBytes = 1,
        });
        using PbtRetainedTestStore store = new();
        using (PbtSnapshot source = PersistenceSnapshot(0, 1, harness.Pool))
        using (PbtRetainedSnapshot retained = store.Build(source)) harness.Repository.TryAddRetained(retained);
        for (int b = 2; b <= 3; b++)
        {
            PbtSnapshot snapshot = PersistenceSnapshot(b - 1, b, harness.Pool);
            snapshot.Content.Codes[TestItem.KeccakC.ValueHash256] = new(new byte[100]);
            harness.Repository.TryAdd(snapshot);
        }
        using (PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(3)))
            Assert.That(action.Persist, Is.Null);
        harness.Repository.Dispose();
    }

    [Test]
    public async Task Scheduler_single_conversion_bounds_drain_and_recovers_partial_failure([Values] bool failSecond)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactionOffset = 0, MaxInMemoryBaseSnapshotCount = 0 });
        using PbtRetainedTestStore store = new();
        int calls = 0;
        SchedulerLoader loader = new(memory =>
        {
            if (++calls == 2 && failSecond) throw new IOException("injected conversion failure");
            using PbtRetainedSnapshot retained = store.Build(memory);
            return harness.Repository.TryAddRetained(retained);
        });
        SchedulerCompactor compactor = new();
        using PbtPersistenceCoordinator coordinator = new(harness.Config, harness.Finalized, harness.Persistence, harness.Repository,
            harness.Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance, loader, compactor, CancellationToken.None);
        for (int b = 1; b <= 6; b++) harness.Repository.TryAdd(PersistenceSnapshot(b - 1, b, harness.Pool));
        if (failSecond) Assert.That(async () => await coordinator.CheckPersistenceAsync(PersistenceState(6)), Throws.TypeOf<IOException>());
        else await coordinator.CheckPersistenceAsync(PersistenceState(6));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Repository.Count, Is.EqualTo(failSecond ? 5 : 2));
            Assert.That(compactor.Enqueued.Count, Is.EqualTo(failSecond ? 1 : 4));
            Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
        }
        await coordinator.CheckPersistenceAsync(PersistenceState(6));
        await coordinator.CheckPersistenceAsync(PersistenceState(6));
        Assert.That(harness.Repository.Count, Is.Zero);
        Assert.That(compactor.Enqueued, Has.Count.EqualTo(6));
        harness.Repository.Dispose();
    }

    [TestCase(false, false, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    public void Scheduler_finality_pruning_verifies_anchors_and_committed_ancestry(bool conflictingAnchor, bool headOnFork, bool prunes)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactionOffset = 0 });
        using PbtRetainedTestStore store = new();
        StateId fork = new(1, TestItem.KeccakB.ValueHash256);
        using (PbtSnapshot snapshot = new(PersistenceState(0), fork, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing))
        using (PbtRetainedSnapshot retained = store.Build(snapshot)) harness.Repository.TryAddRetained(retained);
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 2, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(2, 3, harness.Pool));
        if (headOnFork)
        {
            StateId forkHead = new(3, TestItem.KeccakB.ValueHash256);
            harness.Repository.TryAdd(new(fork, forkHead, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        }
        harness.Finalized.FinalizedBlockNumber = 2;
        harness.Finalized.SetCanonicalRoot(1, TestItem.KeccakA);
        if (conflictingAnchor) harness.Finalized.SetCanonicalRoot(2, TestItem.KeccakC);
        harness.Repository.RemoveFinalizedRetainedForks(PersistenceState(0), harness.Finalized);
        Assert.That(harness.Repository.HasState(fork), Is.EqualTo(!prunes));
        harness.Repository.Dispose();
    }

    [Test]
    public async Task Pipeline_inline_compaction_and_explicit_flush_clear_caches()
    {
        using CoordinatorHarness harness = new(new PbtConfig { InlineCompaction = true, CompactSize = 2, CompactionOffset = 0 });
        IPbtTrieNodeCache cache = Substitute.For<IPbtTrieNodeCache>();
        await using PbtDbManager manager = harness.CreateManager(new ProcessExitSource(CancellationToken.None), LimboLogs.Instance, cache);
        manager.AddSnapshot(PersistenceSnapshot(0, 1, harness.Pool), harness.Pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
        manager.AddSnapshot(PersistenceSnapshot(1, 2, harness.Pool), harness.Pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
        Assert.That(harness.Repository.CompactedCount, Is.EqualTo(1), "inline compaction is complete at admission return");
        cache.Received(2).Add(Arg.Any<PbtTransientResource>());
        manager.FlushCache(CancellationToken.None);
        cache.Received(1).Clear();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        manager.FlushCache(canceled.Token);
        cache.Received(1).Clear();
    }

    [Test]
    public void Scheduler_finality_pruning_work_is_bounded_by_finalized_range([Values(0, 128)] int unrelatedStates)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactionOffset = 0 });
        using PbtRetainedTestStore store = new();
        for (int b = 1; b <= unrelatedStates + 1; b++)
        {
            using PbtSnapshot snapshot = PersistenceSnapshot(b - 1, b, harness.Pool);
            using PbtRetainedSnapshot retained = store.Build(snapshot);
            harness.Repository.TryAddRetained(retained);
        }
        harness.Repository.TryAdd(PersistenceSnapshot(1, 1000, harness.Pool));
        harness.Finalized.FinalizedBlockNumber = 1;
        harness.Finalized.SetCanonicalRoot(1, TestItem.KeccakA);
        harness.Repository.RemoveFinalizedRetainedForks(PersistenceState(0), harness.Finalized);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10; iteration++)
            harness.Repository.RemoveFinalizedRetainedForks(PersistenceState(0), harness.Finalized);
        long perPass = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(perPass, Is.LessThan(2048), "unfinalized history must not be allocated or sorted by the pruning pass");
            using ArrayPoolList<StateId> finalized = harness.Repository.GetRetainedStatesInRange(0, 1);
            Assert.That(finalized, Is.EqualTo(new[] { PersistenceState(1) }));
            Assert.That(harness.Repository.RetainedCount, Is.EqualTo(unrelatedStates + 1));
        }
        harness.Repository.Dispose();
    }

    [Test]
    public async Task Gather_exhaustion_for_available_state_is_not_a_retention_error()
    {
        using CoordinatorHarness harness = new(new PbtConfig());
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.CurrentState.Returns(new StateId(0, TestItem.KeccakB.ValueHash256));
        harness.Persistence.CreateReader().Returns(reader);
        await using PbtDbManager manager = harness.CreateManager(new ProcessExitSource(CancellationToken.None), LimboLogs.Instance, NoopPbtTrieNodeCache.Instance);
        Assert.That(async () =>
        {
            using PbtReadOnlySnapshotBundle bundle = await Task.Run(() => manager.GatherReadOnlyBundle(PersistenceState(1)))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }, Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Timed out gathering"));
        Assert.That(manager.HasStateForBlock(PersistenceState(1)), Is.True);
    }

    [Test]
    public async Task Rewind_then_commit_drops_abandoned_branch_and_invalidates_cached_bundles()
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 1, MinReorgDepth = 0, CompactionOffset = 0 });
        await using PbtDbManager manager = harness.CreateManager(new ProcessExitSource(CancellationToken.None), LimboLogs.Instance, NoopPbtTrieNodeCache.Instance);
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 2, harness.Pool));
        using PbtReadOnlySnapshotBundle held = manager.GatherReadOnlyBundle(PersistenceState(2));
        using PbtReadOnlySnapshotBundle priorHead = manager.GatherReadOnlyBundle(PersistenceState(1));
        manager.DropStateNotReachableFrom(PersistenceState(1));
        using PbtReadOnlySnapshotBundle freshHead = manager.GatherReadOnlyBundle(PersistenceState(1));
        StateId newBranch = new(2, TestItem.KeccakB.ValueHash256);
        harness.Repository.TryAdd(new(PersistenceState(1), newBranch, TestItem.KeccakC.ValueHash256,
            new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        using PbtReadOnlySnapshotBundle freshBranch = manager.GatherReadOnlyBundle(newBranch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.HasStateForBlock(PersistenceState(2)), Is.False);
            Assert.That(manager.TryGatherReadOnlyBundle(PersistenceState(2)), Is.Null);
            Assert.That(freshHead, Is.Not.SameAs(priorHead));
            Assert.That(held.TreeRoot, Is.EqualTo(TestItem.KeccakA.ValueHash256));
            Assert.That(freshBranch.TreeRoot, Is.EqualTo(TestItem.KeccakC.ValueHash256));
            Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
        }
        harness.Coordinator.FlushToPersistence(CancellationToken.None);
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(newBranch));
    }

    [Test]
    public async Task Rewind_failed_retained_delete_invalidates_cached_pruned_memory_but_preserves_held_reader()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-rewind-fault-" + Guid.NewGuid().ToString("N"));
        using MemDb db = new();
        RewindFaultCatalog fault = new(new PbtSnapshotCatalog(db));
        try
        {
            await using IContainer container = RetainedContainer(path, db, configure: builder =>
                builder.RegisterInstance(fault).Keyed<ISnapshotCatalog>(DbNames.Pbt));
            IPbtDbManager manager = container.Resolve<IPbtDbManager>();
            PbtSnapshotRepository repository = container.Resolve<PbtSnapshotRepository>();
            repository.TryAdd(LoaderSnapshot(container, StateId.PreGenesis, PersistenceState(1)));
            repository.TryAdd(LoaderSnapshot(container, PersistenceState(1), PersistenceState(3)));
            PbtSnapshot orphan = LoaderSnapshot(container, PersistenceState(1), PersistenceState(2));
            orphan.TryLease();
            repository.TryAdd(orphan);
            using (orphan) container.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(orphan);
            repository.RemoveMemoryState(PersistenceState(2));
            using PbtReadOnlySnapshotBundle held = manager.GatherReadOnlyBundle(PersistenceState(3));
            fault.FailRemove = true;
            Assert.Throws<IOException>(() => manager.DropStateNotReachableFrom(PersistenceState(1)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.HasStateForBlock(PersistenceState(3)), Is.False);
                Assert.That(manager.TryGatherReadOnlyBundle(PersistenceState(3)), Is.Null);
                Assert.That(manager.HasStateForBlock(PersistenceState(2)), Is.True);
                Assert.That(held.GetCode(TestItem.KeccakC.ValueHash256)!.CodeSpan.Length, Is.EqualTo(65537));
                Assert.That(repository.GetLastCommittedStateId(), Is.EqualTo(PersistenceState(2)));
            }
            fault.FailRemove = false;
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class RewindFaultCatalog(ISnapshotCatalog inner) : ISnapshotCatalog
    {
        internal bool FailRemove { get; set; }
        public void Add(CatalogEntry entry) => inner.Add(entry);
        public bool Remove(in StateId to, long depth) => FailRemove ? throw new IOException("injected rewind catalog delete failure") : inner.Remove(to, depth);
        public IEnumerable<CatalogEntry> Load() => inner.Load();
    }

    [Test]
    public void Rewind_pending_backstop_uses_reset_committed_seed()
    {
        using CoordinatorHarness harness = new(new PbtConfig { EnableLongFinality = false, CompactSize = 1, MinReorgDepth = 0, MaxReorgDepth = 0, CompactionOffset = 0 });
        StateId reset = new(1, TestItem.KeccakB.ValueHash256);
        harness.Repository.TryAdd(new(PersistenceState(0), reset, default, new(), harness.Pool, PbtResourcePool.Usage.MainBlockProcessing));
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 5, harness.Pool));
        Assert.That(harness.Coordinator.DropStateNotReachableFrom(reset), Is.True);
        using PbtPersistenceCoordinator.SnapshotAction action = harness.Coordinator.DetermineSnapshotAction(PersistenceState(5));
        Assert.That(action.Persist!.To, Is.EqualTo(reset));
        Assert.That(harness.Repository.HasState(PersistenceState(5)), Is.False);
    }

    [Test]
    public async Task Rewind_is_serialized_with_base_commit()
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 1, MinReorgDepth = 0, CompactionOffset = 0 });
        using ManualResetEventSlim committing = new();
        using ManualResetEventSlim resume = new();
        using ManualResetEventSlim resetStarted = new();
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Batch.When(batch => batch.Commit()).Do(_ =>
        {
            committing.Set();
            Assert.That(resume.Wait(TimeSpan.FromSeconds(10)), Is.True);
        });
        Task persist = Task.Run(() => harness.Coordinator.FlushToPersistence(CancellationToken.None));
        Assert.That(committing.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Task<bool> reset = Task.Run(() =>
        {
            resetStarted.Set();
            return harness.Coordinator.DropStateNotReachableFrom(PersistenceState(0));
        });
        try
        {
            Assert.That(resetStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(reset.IsCompleted, Is.False);
            Assert.That(harness.Repository.HasState(PersistenceState(1)), Is.True);
        }
        finally { resume.Set(); }
        await persist.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(await reset.WaitAsync(TimeSpan.FromSeconds(10)), Is.False, "the new irreversible base rejects the old reset target");
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(1)));
    }

    [Test]
    public async Task Rewind_is_serialized_with_conversion()
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 1, MaxInMemoryBaseSnapshotCount = 0, MinReorgDepth = 128, CompactionOffset = 0 });
        using PbtRetainedTestStore store = new();
        using ManualResetEventSlim converting = new();
        using ManualResetEventSlim resume = new();
        using ManualResetEventSlim resetStarted = new();
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 2, harness.Pool));
        SchedulerLoader loader = new(snapshot =>
        {
            converting.Set();
            Assert.That(resume.Wait(TimeSpan.FromSeconds(10)), Is.True);
            using PbtRetainedSnapshot retained = store.Build(snapshot);
            return harness.Repository.TryAddRetained(retained);
        });
        using PbtPersistenceCoordinator coordinator = new(harness.Config, harness.Finalized, harness.Persistence, harness.Repository,
            harness.Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance, loader, new SchedulerCompactor(), CancellationToken.None);
        Task persistence = Task.Run(() => coordinator.CheckPersistenceAsync(PersistenceState(2)));
        Assert.That(converting.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Task<bool> reset = Task.Run(() =>
        {
            resetStarted.Set();
            return coordinator.DropStateNotReachableFrom(PersistenceState(1));
        });
        try
        {
            Assert.That(resetStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(reset.IsCompleted, Is.False);
        }
        finally { resume.Set(); }
        await persistence.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(await reset.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Repository.HasState(PersistenceState(1)), Is.True);
            Assert.That(harness.Repository.HasState(PersistenceState(2)), Is.False);
            Assert.That(harness.Repository.GetLastCommittedStateId(), Is.EqualTo(PersistenceState(1)));
            Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
        }
        harness.Repository.Dispose();
    }

    [Test]
    public async Task Rewind_after_retained_restart_prunes_durably_and_forwards_world_state()
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-rewind-reopen-" + Guid.NewGuid().ToString("N"));
        using MemDb catalog = new();
        StateId reset = PersistenceState(1);
        StateId abandoned = PersistenceState(2);
        try
        {
            await using (IContainer initial = RetainedContainer(path, catalog))
            {
                initial.Resolve<IPbtDbManager>();
                foreach ((StateId from, StateId to) in new[] { (StateId.PreGenesis, reset), (reset, abandoned) })
                {
                    PbtSnapshot snapshot = LoaderSnapshot(initial, from, to);
                    snapshot.TryLease();
                    initial.Resolve<PbtSnapshotRepository>().TryAdd(snapshot);
                    using (snapshot) initial.Resolve<IPbtRetainedSnapshotLoader>().ConvertAndRegister(snapshot);
                }
            }
            await using (IContainer reopened = RetainedContainer(path, catalog))
            {
                IPbtDbManager manager = reopened.Resolve<IPbtDbManager>();
                using PbtReadOnlySnapshotBundle held = manager.GatherReadOnlyBundle(abandoned);
                BlockHeader head = Build.A.BlockHeader.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
                reopened.Resolve<ScopeProvider.PbtWorldStateManager>().DropStateNotReachableFrom(head);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(manager.HasStateForBlock(abandoned), Is.False);
                    Assert.That(manager.HasStateForBlock(reset), Is.True);
                    Assert.That(reopened.Resolve<PbtSnapshotRepository>().GetLastCommittedStateId(), Is.EqualTo(reset));
                    Assert.That(held.GetCode(TestItem.KeccakC.ValueHash256)!.CodeSpan.Length, Is.EqualTo(65537));
                    Assert.That(reopened.Resolve<PbtPersistenceCoordinator>().GetCurrentPersistedStateId(), Is.EqualTo(StateId.PreGenesis));
                }
            }
            await using IContainer final = RetainedContainer(path, catalog);
            IPbtDbManager finalManager = final.Resolve<IPbtDbManager>();
            Assert.That(finalManager.HasStateForBlock(reset), Is.True);
            Assert.That(finalManager.HasStateForBlock(abandoned), Is.False);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class SchedulerLoader(Func<PbtSnapshot, bool> convert) : IPbtRetainedSnapshotLoader
    {
        public void Load() { }
        public bool ConvertAndRegister(PbtSnapshot snapshot) => convert(snapshot);
        public bool RegisterCompacted(PbtRetainedSnapshot snapshot, ReadOnlySpan<PbtRetainedSnapshot> sources) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class SchedulerCompactor : IPbtRetainedSnapshotCompactor
    {
        internal List<StateId> Enqueued { get; } = [];
        public ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, ulong persistedBlockNumber, CancellationToken cancellationToken)
        {
            Enqueued.AddRange(batch);
            batch.Dispose();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CoordinatorHarness : IDisposable
    {
        public CoordinatorHarness(PbtConfig config)
        {
            Config = config;
            Pool = new(config);
            Schedule = PbtCoreRegistration.CreateCompactionSchedule(Metadata, config, LimboLogs.Instance);
            IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
            reader.CurrentState.Returns(PersistenceState(0));
            Persistence.CreateReader().Returns(reader);
            Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>()).Returns(Batch);
            Coordinator = new(config, Finalized, Persistence, Repository, Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        }

        public PbtConfig Config { get; }
        public PbtResourcePool Pool { get; }
        public PbtSnapshotRepository Repository { get; } = new(new MetricsConfig());
        public MemDb Metadata { get; } = new();
        public ICompactionSchedule Schedule { get; }
        public PbtTestContext.TestFinalizedStateProvider Finalized { get; } = new();
        public IPbtPersistence Persistence { get; } = Substitute.For<IPbtPersistence>();
        public IPbtPersistence.IWriteBatch Batch { get; } = Substitute.For<IPbtPersistence.IWriteBatch>();
        public PbtPersistenceCoordinator Coordinator { get; }

        public PbtDbManager CreateManager(IProcessExitSource exitSource, ILogManager logs, IPbtTrieNodeCache trieNodeCache) =>
            new(Repository, Coordinator, Persistence, Pool, new PbtSnapshotCompactor(Pool, Schedule, Repository, Config, logs), exitSource, logs,
                new MetricsConfig(), trieNodeCache);

        public void Dispose()
        {
            Repository.Dispose();
            Coordinator.Dispose();
            Metadata.Dispose();
        }
    }

    [TestCase(false, 0, 0, 64, false)]
    [TestCase(false, 0, 0, 64, true)]
    [TestCase(false, 1, 8, 64, false)]
    [TestCase(false, 1, 8, 64, true)]
    [TestCase(true, 0, 0, 4, false)]
    [TestCase(true, 0, 0, 4, true)]
    [TestCase(true, 1, 8, 4, false)]
    [TestCase(true, 1, 8, 4, true)]
    public async Task Differential_histories_match_actions_ranges_tiers_and_available_states(bool longFinality, int offset, int finalized, int memoryLimit, bool bytePressure)
    {
        string path = Path.Combine(Path.GetTempPath(), "pbt-flat-differential-" + Guid.NewGuid().ToString("N"));
        PbtConfig pbtConfig = new()
        {
            Enabled = true, EnableLongFinality = longFinality, CompactSize = 4, CompactionOffset = offset,
            MinReorgDepth = 2, MaxReorgDepth = 6, LongFinalityMaxReorgDepth = 32,
            MaxInMemoryBaseSnapshotCount = memoryLimit, MaxInMemorySnapshotBytes = bytePressure ? 1UL : 0UL, ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576, PersistedSnapshotArenaPageCacheBytes = 0,
        };
        FlatDbConfig flatConfig = new()
        {
            Enabled = true, EnableLongFinality = longFinality, CompactSize = 4, CompactionOffset = offset,
            MinReorgDepth = 2, MaxReorgDepth = 6, LongFinalityMaxReorgDepth = 32,
            MaxInMemoryBaseSnapshotCount = memoryLimit, MaxInMemorySnapshotBytes = bytePressure ? 1UL : 0UL, ArenaFileSizeBytes = 1048576,
            PersistedSnapshotDedicatedArenaThresholdBytes = 1048576, PersistedSnapshotArenaPageCacheBytes = 0,
        };
        PbtTestContext.TestFinalizedStateProvider finality = new() { FinalizedBlockNumber = (ulong)finalized };
        for (ulong height = 0; height <= (ulong)finalized; height++) finality.SetCanonicalRoot(height, TestItem.KeccakA);
        using MemDb pbtCatalog = new(), flatCatalog = new();
        try
        {
            await using IContainer pbt = PbtTestContext.BuildProductionContainer(pbtConfig, builder =>
            {
                builder.RegisterInstance(finality).As<IStateHeaderProvider>();
                builder.RegisterInstance(pbtCatalog).Keyed<IDb>(PbtSnapshotCatalog.DatabaseKey).ExternallyOwned();
                builder.AddSingleton<IPbtRetainedSnapshotCompactor>(NullPbtRetainedSnapshotCompactor.Instance);
            }, new InitConfig { BaseDbPath = Path.Combine(path, "pbt") });
            await using IContainer flat = new ContainerBuilder()
                .AddModule(new FlatWorldStateModule(flatConfig))
                .AddSingleton<IFlatDbConfig>(flatConfig)
                .AddSingleton<IDbFactory, MemDbFactory>()
                .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = Path.Combine(path, "flat") })
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IMetricsConfig>(new MetricsConfig())
                .AddSingleton<IBlocksConfig>(new BlocksConfig())
                .AddSingleton<ISyncConfig>(new SyncConfig())
                .AddSingleton<IProcessExitSource>(new ProcessExitSource(CancellationToken.None))
                .AddSingleton<IStatePersistenceBarrier>(NullStatePersistenceBarrier.Instance)
                .AddSingleton<IStateHeaderProvider>(finality)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, new MemDb())
                .AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, flatCatalog)
                .AddSingleton<IPersistedSnapshotCompactor>(NullPersistedSnapshotCompactor.Instance)
                .Build();
            Assert.That(flat.Resolve<IFlatDbConfig>().EnableLongFinality, Is.EqualTo(longFinality));
            IPbtPersistence pbtPersistence = pbt.Resolve<IPbtPersistence>();
            using (IPbtPersistence.IWriteBatch batch = pbtPersistence.CreateWriteBatch(StateId.PreGenesis, PersistenceState(0), TestItem.KeccakB.ValueHash256, WriteFlags.None)) batch.Commit();
            using (IPersistence.IWriteBatch batch = flat.Resolve<IPersistence>().CreateWriteBatch(StateId.PreGenesis, PersistenceState(0))) { }
            PbtSnapshotRepository pbtRepository = pbt.Resolve<PbtSnapshotRepository>();
            ISnapshotRepository flatRepository = flat.Resolve<ISnapshotRepository>();
            pbt.Resolve<IPbtRetainedSnapshotLoader>().Load();
            flat.Resolve<IPersistedSnapshotLoader>().Load();
            PbtPersistenceCoordinator pbtCoordinator = pbt.Resolve<PbtPersistenceCoordinator>();
            IPersistenceManager flatCoordinator = flat.Resolve<IPersistenceManager>();
            IPbtResourcePool pbtPool = pbt.Resolve<IPbtResourcePool>();
            IResourcePool flatPool = flat.Resolve<IResourcePool>();
            List<StateId> states = [];
            for (int height = 1; height <= 12; height++)
            {
                StateId state = PersistenceState(height);
                AddDifferentialEdge(PersistenceState(height - 1), state, false);
                states.Add(state);
                if (height >= 4 && (height - offset) % 4 == 0)
                    AddDifferentialEdge(PersistenceState(height - 4), state, true);
                if (height == 3)
                {
                    StateId fork = new(3, TestItem.KeccakC.ValueHash256);
                    AddDifferentialEdge(PersistenceState(2), fork, false);
                    states.Add(fork);
                    pbtRepository.SetLastCommittedStateId(state);
                    flatRepository.SetLastCommittedStateId(state);
                }
                Assert.That(NormalizePbtAction(pbtCoordinator, state), Is.EqualTo(NormalizeFlatAction(flatCoordinator, state)), $"selected action at {state}");
                await pbtCoordinator.CheckPersistenceAsync(state);
                await flatCoordinator.AddToPersistence(state);
                Assert.That(pbtCoordinator.GetCurrentPersistedStateId(), Is.EqualTo(flatCoordinator.GetCurrentPersistedStateId()), $"base after {state}");
                foreach (StateId known in states)
                    Assert.That(pbtRepository.HasState(known) || known == pbtCoordinator.GetCurrentPersistedStateId(),
                        Is.EqualTo(flatRepository.HasState(known) || known == flatCoordinator.GetCurrentPersistedStateId()), $"availability {known} after {state}");
            }
            StateId reset = PersistenceState(10);
            Assert.That(pbtCoordinator.DropStateNotReachableFrom(reset), Is.True);
            flatCoordinator.DropStateNotReachableFrom(reset);
            Assert.That(pbtRepository.GetLastCommittedStateId(), Is.EqualTo(flatRepository.GetLastCommittedStateId()));
            AddDifferentialEdge(reset, new(11, TestItem.KeccakD.ValueHash256), false);
            Assert.That(pbtCoordinator.FlushToPersistenceState(CancellationToken.None), Is.EqualTo(flatCoordinator.FlushToPersistence(CancellationToken.None)));
            using IPbtPersistence.IReader persistedPbt = pbtPersistence.CreateReader();
            Assert.That(persistedPbt.GetAccount(PbtStateKey.AddressKeyHash(TestItem.AddressA))!.Value.ToAccount().Nonce, Is.EqualTo((ulong)11));
            Assert.That(persistedPbt.CurrentRoot, Is.EqualTo(TestItem.KeccakB.ValueHash256), "PBT root is independent of shared header StateId");

            void AddDifferentialEdge(StateId from, StateId to, bool compacted)
            {
                PbtSnapshotContent content = pbtPool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
                content.Accounts[PbtStateKey.AddressKeyHash(TestItem.AddressA)] = PbtAccount.From(new Account(to.BlockNumber, 100), null);
                PbtSnapshot pbtSnapshot = new(from, to, TestItem.KeccakB.ValueHash256, content, pbtPool, PbtResourcePool.Usage.MainBlockProcessing);
                if (compacted) pbtRepository.TryAddCompacted(pbtSnapshot); else pbtRepository.TryAdd(pbtSnapshot);
                Snapshot flatSnapshot = flatPool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
                flatSnapshot.Content.Accounts[TestItem.AddressA] = new Account(to.BlockNumber, 100);
                flatRepository.TryAdd(flatSnapshot, compacted ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase);
                flatRepository.AddStateId(to);
                if (!compacted) flatRepository.SetLastCommittedStateId(to);
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private readonly record struct DifferentialAction(string Kind, StateId From, StateId To, SnapshotTier Tier);

    private static DifferentialAction NormalizePbtAction(PbtPersistenceCoordinator coordinator, StateId state)
    {
        using PbtPersistenceCoordinator.SnapshotAction action = coordinator.DetermineSnapshotAction(state);
        if (action.Persist is { } persist) return new("persist", persist.From, persist.To, persist.Tier);
        if (action.Convert is { } convert) return new(action.ConvertRange ? "convert-range" : "convert", convert.From, convert.To,
            action.ConvertRange ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase);
        return default;
    }

    private static DifferentialAction NormalizeFlatAction(IPersistenceManager coordinator, StateId state)
    {
        MethodInfo method = coordinator.GetType().GetMethod("DetermineSnapshotAction", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ITuple action = (ITuple)method.Invoke(coordinator, [state])!;
        using PersistedSnapshot? retained = action[0] as PersistedSnapshot;
        using Snapshot? memory = action[1] as Snapshot;
        object? conversion = action[2];
        if (retained is not null)
        {
            PropertyInfo tier = typeof(PersistedSnapshot).GetProperty("Tier", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return new("persist", retained.From, retained.To, (SnapshotTier)tier.GetValue(retained)!);
        }
        if (memory is not null) return new("persist", memory.From, memory.To,
            memory.To.BlockNumber - memory.From.BlockNumber > 1 ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase);
        if (conversion is null) return default;
        using Snapshot? compacted = conversion.GetType().GetProperty("Compacted")!.GetValue(conversion) as Snapshot;
        using Snapshot? single = conversion.GetType().GetProperty("Base")!.GetValue(conversion) as Snapshot;
        Snapshot selected = compacted ?? single!;
        return new(compacted is not null ? "convert-range" : "convert", selected.From, selected.To,
            compacted is not null ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase);
    }

    private static PbtSnapshot PersistenceSnapshot(int from, int to, PbtResourcePool pool) =>
        new(PersistenceState(from), PersistenceState(to), TestItem.KeccakA.ValueHash256,
            pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), pool, PbtResourcePool.Usage.MainBlockProcessing);

}
