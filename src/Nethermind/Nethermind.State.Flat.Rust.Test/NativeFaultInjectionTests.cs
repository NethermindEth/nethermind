// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;
using NUnit.Framework;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// Faults injected into the native library come out in C# as the C# backend's would: a fatal RocksDB error in a
/// persist writes nothing, freezes the persisted state and shuts down through
/// <see cref="DbOnTheRocks.HandleFatalDbError"/>; a flush error that is not fatal is logged and tolerated; a panic
/// anywhere poisons the store, so every later call fails fast while disposal still releases everything; and a throwing
/// host callback aborts the persist.
/// </summary>
/// <remarks>Faults are injected through the test-api build of the library; see <see cref="NativeFaults"/>.</remarks>
[Parallelizable(ParallelScope.All)]
public class NativeFaultInjectionTests
{
    private const int EventuallyMs = 30_000;
    private const int PollMs = 10;

    public enum PanicSite
    {
        Export,
        PersistWorker,
        CompactorWorker,
    }

    /// <summary>
    /// A fatal RocksDB error writing a persist, on the flushing thread or on the persistence worker, writes nothing,
    /// shuts down through <see cref="DbOnTheRocks.HandleFatalDbError"/> and freezes the persisted state: every later
    /// persist fails with it.
    /// </summary>
    [Test]
    public async Task A_fatal_error_in_a_persist_freezes_the_persisted_state_and_shuts_down([Values] bool onWorker)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, finality);
        for (ulong number = 0; number <= 5; number++) container.AddBlock(number);
        container.Arm.FlushCache(CancellationToken.None);
        List<string> columns = FlatTestDbs.DumpAll(container.Db);

        NativeFaults.Inject(container.Resolve<RustPersistence>().Store, NativeFaults.Fault.KvWrite, "IO error: injected");
        // Block 8, the next boundary, finalized makes the persistence worker persist once the head reaches block 10.
        if (onWorker) finality.Finalize(8, Block);
        for (ulong number = 6; number <= 10; number++) container.AddBlock(number);
        if (onWorker) Assert.That(() => container.Db.FatalShutdowns, Is.Positive.After(EventuallyMs, PollMs), "the worker's error reached C#");
        Exception? flushFailure = Catch(() => container.Arm.FlushCache(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flushFailure, Is.InstanceOf<RocksDbException>().With.Message.EqualTo("IO error: injected"));
            Assert.That(container.Db.FatalShutdowns, Is.Positive);
            Assert.That(container.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(Block(5)));
            Assert.That(FlatTestDbs.DumpAll(container.Db), Is.EqualTo(columns), "nothing was written");
        }
    }

    /// <summary>
    /// A fatal RocksDB error in the persist the shutdown drains shuts down through
    /// <see cref="DbOnTheRocks.HandleFatalDbError"/> before the disposal returns, as on the C# backend, whose drain hits
    /// it on the persistence task the disposal awaits, so the corruption marker is written before the node exits.
    /// </summary>
    [Test]
    public async Task A_fatal_error_in_the_persist_the_shutdown_drains_is_reported_before_the_disposal_returns()
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestPersistenceBarrier barrier = new();
        TestFinality finality = new();
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, finality, new() { PersistenceBarrier = barrier });
        container.Db.FatalShuttingDown = static () => Thread.Sleep(200);
        for (ulong number = 0; number <= 3; number++) container.AddBlock(number);
        container.Arm.FlushCache(CancellationToken.None);

        // Block 4, the next boundary, finalized makes the persistence worker persist once the head reaches block 6.
        finality.Finalize(4, Block);
        barrier.Hold();
        for (ulong number = 4; number <= 8; number++) container.AddBlock(number);
        bool persistHeld = barrier.WaitHeld(TimeSpan.FromMilliseconds(EventuallyMs));
        NativeFaults.Inject(container.Resolve<RustPersistence>().Store, NativeFaults.Fault.KvWrite, "IO error: injected");

        Task disposal = Task.Run(async () => await container.DisposeAsync());
        barrier.Release();
        await disposal;

        Assert.That(persistHeld, Is.True, "the persist waits in the barrier until the shutdown drains it");
        Assert.That(container.Db.FatalShutdowns, Is.EqualTo(1));
    }

    /// <summary>A failed flush is logged and tolerated, as <see cref="DbOnTheRocks"/> tolerates it, unless the error is fatal.</summary>
    [TestCase("Busy: injected", false)]
    [TestCase("IO error: injected", true)]
    public void A_failed_flush_is_tolerated_unless_fatal(string error, bool fatal)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using FlatTestDbs.TrackingColumnsDb db = new(path.Path);
        RecordingLogManager logManager = new(LogLevel.Warn);
        using RustPersistence persistence = new(db, new FlatDbConfig(), logManager);
        NativeFaults.Inject(persistence.Store, NativeFaults.Fault.KvFlush, error);

        Exception? failure = Catch(persistence.Flush);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, fatal ? Is.InstanceOf<RocksDbException>().With.Message.EqualTo(error) : Is.Null);
            Assert.That(db.FatalShutdowns, Is.EqualTo(fatal ? 1 : 0));
            Assert.That(logManager.Lines, fatal ? Is.Empty : Is.EqualTo(new[] { $"Warn Db.Rocks.DbOnTheRocks: Failed to flush Flat DB: {error}" }));
        }
    }

    /// <summary>
    /// A panic inside an export, or in a job of the persistence or the compaction worker, poisons the store: every later
    /// call fails fast with <see cref="FdbStatus.Poisoned"/>, through a bundle gathered before too, while disposal
    /// still releases everything, and the database keeps what was persisted before.
    /// </summary>
    [Test]
    public async Task A_panic_poisons_the_store_for_every_later_call([Values] PanicSite site)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatDbConfig config = FlatManagerContainer.Config(FlatDbBackend.Rust);
        config.InlineCompaction = site != PanicSite.CompactorWorker;
        FlatManagerContainer container = new(config, path.Path, finality);
        container.AddBlock(0);
        container.Arm.FlushCache(CancellationToken.None);
        IBundleView held = container.Arm.GatherReadOnly(Block(0), ReaderFlags.None);
        RustPersistence persistence = container.Resolve<RustPersistence>();

        // Block 4, the next boundary, finalized makes the persistence worker persist once the head reaches block 6.
        finality.Finalize(4, Block);
        NativeFaults.Inject(persistence.Store, site switch
        {
            PanicSite.Export => NativeFaults.Fault.PanicInExport,
            PanicSite.PersistWorker => NativeFaults.Fault.PanicInPersist,
            _ => NativeFaults.Fault.PanicInCompaction,
        }, "injected");
        FdbStatus? first = Status(() => container.Arm.HasStateForBlock(Block(0)));
        for (ulong number = 1; number <= 6 && site != PanicSite.Export; number++)
        {
            ulong block = number;
            Catch(() => container.AddBlock(block));
        }

        Assert.That(() => Status(() => container.Arm.HasStateForBlock(Block(0))), Is.EqualTo(FdbStatus.Poisoned).After(EventuallyMs, PollMs));
        FdbStatus?[] later =
        [
            Status(() => container.Arm.HasStateForBlock(Block(0))),
            Status(() => container.AddBlock(7)),
            Status(() => container.Arm.GatherReadOnly(Block(0), ReaderFlags.None).Dispose()),
            Status(() => container.Arm.FlushCache(CancellationToken.None)),
            Status(() => container.Arm.DropStateNotReachableFrom(Block(0))),
            Status(() => container.PersistenceManager.GetCurrentPersistedStateId()),
            Status(() => persistence.CreateReader().Dispose()),
            Status(() => held.GetAccount(BlockAddress)),
        ];
        Exception? disposalFailure = await CatchAsync(async () =>
        {
            held.Dispose();
            await container.DisposeAsync();
        });

        await using FlatManagerContainer reopened = new(FlatManagerContainer.Config(FlatDbBackend.CSharp), path.Path, finality);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(site == PanicSite.Export ? FdbStatus.Panic : null), "the export panicked");
            Assert.That(later, Is.All.EqualTo(FdbStatus.Poisoned));
            Assert.That(disposalFailure, Is.Null);
            Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
            Assert.That(reopened.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(Block(0)), "the panicking persist wrote nothing");
        }
    }

    /// <summary>
    /// A deferred block-data flush that throws aborts the persist, which writes nothing and logs the failure, as on the
    /// C# backend, and the next persist goes through once the flush works again.
    /// </summary>
    [Test]
    public async Task A_throwing_host_callback_aborts_the_persist([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestPersistenceBarrier barrier = new() { Failure = new IOException("injected") };
        RecordingLogManager logManager = new(LogLevel.Error);
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality(), new() { LogManager = logManager, PersistenceBarrier = barrier });
        for (ulong number = 0; number <= 5; number++) container.AddBlock(number);
        List<string> columns = FlatTestDbs.DumpAll(container.Db);

        Exception? failure = Catch(() => container.Arm.FlushCache(CancellationToken.None));
        StateId persistedAfterFailure = container.PersistenceManager.GetCurrentPersistedStateId();
        List<string> columnsAfterFailure = FlatTestDbs.DumpAll(container.Db);
        if (backend == FlatDbBackend.Rust) container.Resolve<RustPersistence>().Store.PumpLogs();
        barrier.Failure = null;
        container.Arm.FlushCache(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.Not.Null);
            Assert.That(persistedAfterFailure, Is.EqualTo(StateId.PreGenesis));
            Assert.That(columnsAfterFailure, Is.EqualTo(columns), "nothing was written");
            Assert.That(logManager.Lines, Has.Some.StartsWith("Error State.Flat.PersistenceManager: Block-data flush failed before persisting state"));
            Assert.That(container.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(Block(5)), "the flush works again");
        }
    }

    /// <summary>Where a scope or read-only bundle call panics.</summary>
    public enum BundleCall
    {
        SetAccount,
        SetSlot,
        SetStateNode,
        PutStorageNodes,
        Commit,
        LoadFromReadOnlyBundle,
    }

    /// <summary>
    /// A panic inside a write, a commit or a load of a native scope or read-only bundle poisons the store: the call fails
    /// with <see cref="FdbStatus.Panic"/> and every later one, of the same scope or of the manager, with
    /// <see cref="FdbStatus.Poisoned"/>, while disposal still releases everything.
    /// </summary>
    [Test]
    public async Task A_panic_in_a_scope_or_bundle_call_poisons_the_store([Values] BundleCall call)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        container.AddBlock(0);
        Fork.IFlatDbManager manager = container.Resolve<Fork.IFlatDbManager>();
        Fork.SnapshotBundle bundle = manager.GatherSnapshotBundle(Block(0), ResourcePool.Usage.MainBlockProcessing);
        Fork.ReadOnlySnapshotBundle readOnly = manager.GatherReadOnlySnapshotBundle(Block(0));
        TrieNode node = new(NodeType.Leaf, Keccak.Compute("node"), [0xc1, 0x01]);

        NativeFaults.Inject(container.Resolve<RustPersistence>().Store, NativeFaults.Fault.PanicInExport, "injected");
        FdbStatus? panicked = Status(() =>
        {
            switch (call)
            {
                case BundleCall.SetAccount: bundle.SetAccount(BlockAddress, new Account(1, 1)); break;
                case BundleCall.SetSlot: bundle.SetChangedSlot(BlockAddress, 1, 1); break;
                case BundleCall.SetStateNode: bundle.SetStateNode(TreePath.Empty, node); break;
                case BundleCall.PutStorageNodes: bundle.PublishStorageNodes(bundle.GetStorageNodeDestination(Keccak.Zero), Keccak.Zero, [[(TreePath.Empty, node)]]); break;
                case BundleCall.Commit: bundle.CollectAndApplySnapshot(Block(0), Block(1)); break;
                default: readOnly.TryLoadStateRlp(TreePath.Empty, Keccak.Zero, ReadFlags.None); break;
            }
        });
        FdbStatus?[] later =
        [
            Status(() => bundle.GetAccount(BlockAddress)),
            Status(() => readOnly.GetAccount(BlockAddress)),
            Status(() => manager.HasStateForBlock(Block(0))),
        ];
        Exception? disposalFailure = await CatchAsync(async () =>
        {
            bundle.Dispose();
            readOnly.Dispose();
            await container.DisposeAsync();
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panicked, Is.EqualTo(FdbStatus.Panic));
            Assert.That(later, Is.All.EqualTo(FdbStatus.Poisoned));
            Assert.That(disposalFailure, Is.Null);
            Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
        }
    }

    /// <summary>A late hint or read of a disposed scope bundle, as a prewarmer racing the scope's end makes, fails as disposed.</summary>
    [Test]
    public async Task A_late_call_of_a_disposed_scope_bundle_fails_as_disposed()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        container.AddBlock(0);
        Fork.SnapshotBundle bundle = container.Resolve<Fork.IFlatDbManager>().GatherSnapshotBundle(Block(0), ResourcePool.Usage.MainBlockProcessing);
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => bundle.PromoteAccount(BlockAddress, new Account(1, 1)), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => bundle.GetAccount(BlockAddress, out _), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => bundle.DetermineSelfDestructSnapshotIdx(BlockAddress), Throws.Nothing, "no clear below the scope, so no call");
            Assert.That(() => bundle.SetChangedSlot(BlockAddress, 1, 1), Throws.TypeOf<ObjectDisposedException>());
        }
    }

    /// <summary>
    /// A deleted arena file of the persisted-snapshot tier drops the snapshots catalogued in it at the next start, which
    /// goes on reading through the rest of the tier and never gives a new arena file the id of a missing one.
    /// </summary>
    [Test]
    public async Task A_missing_arena_file_drops_its_snapshots_and_its_id_stays_unused([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        await using (FlatManagerContainer first = new(StalledChain.Config(backend), path.Path, finality, overrides))
        {
            StalledChain.Add(first, finality, 0, 23);
        }

        CatalogEntry[] rows = StalledChain.Catalog(path.Path);
        int[] merged = [.. rows.Where(static row => row.Tier == SnapshotTier.PersistedCompactSized).Select(static row => row.Location.ArenaId)];
        Assert.That(merged, Is.Not.Empty, "the tier merged a compact-sized snapshot");
        int lastIdBeforeRestart = StalledChain.ArenaIds(path.Path)[^1];
        File.Delete(StalledChain.ArenaFile(path.Path, merged[^1]));

        await using FlatManagerContainer reopened = new(StalledChain.Config(backend), path.Path, finality, overrides);
        CatalogEntry[] bases = [.. rows.Where(static row => row.Tier == SnapshotTier.PersistedBase)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reopened.PersistedSnapshotCount, Is.EqualTo((long)rows.Count(row => row.Location.ArenaId != merged[^1])), "the snapshots of the deleted file were dropped");
            foreach (CatalogEntry row in bases)
            {
                Assert.That(StalledChain.Reads(reopened, row.To), Is.EqualTo(StalledChain.ExpectedReads(row.To.BlockNumber)), $"reads {row.To}");
            }
        }

        int[] idsBefore = StalledChain.ArenaIds(path.Path);
        ulong resume = StalledChain.Held(reopened, 23)[^1];
        StalledChain.Add(reopened, finality, resume + 1, resume + 12);
        int[] newIds = [.. StalledChain.ArenaIds(path.Path).Except(idsBefore)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bases, Is.Not.Empty);
            Assert.That(newIds, Is.Not.Empty, "the next conversions opened new arena files");
            Assert.That(newIds, Is.All.GreaterThan(lastIdBeforeRestart), "no id is used again");
        }
    }

    /// <summary>How the files or the catalog of a persisted-snapshot tier are damaged between two runs.</summary>
    public enum TierDamage
    {
        /// <summary>Every blob file is gone, which the tables still reference.</summary>
        MissingBlobFiles,

        /// <summary>The format byte ending every table is changed.</summary>
        FormatByte,

        /// <summary>The tier byte of every catalog row is 1, a tier no row is written with.</summary>
        CatalogTierOne,

        /// <summary>The tier byte of every catalog row is 6, past the last tier.</summary>
        CatalogTierSix,
    }

    /// <summary>
    /// A tier whose files or catalog were damaged between two runs refuses startup on the Rust backend with the exception
    /// and message of the C# backend, and the refused start lets the catalog go.
    /// </summary>
    [Test]
    public async Task A_damaged_tier_refuses_startup_as_on_the_csharp_backend([Values] TierDamage damage)
    {
        RustNativeLibrary.Require();

        (Type Type, string Message) expected = await RefusedStartup(FlatDbBackend.CSharp, damage);
        (Type Type, string Message) actual = await RefusedStartup(FlatDbBackend.Rust, damage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Message, Is.EqualTo(expected.Message));
            // The native tier refuses a catalog tier past the last as an invalid argument, an ArgumentException, which
            // the ArgumentOutOfRangeException of the C# backend derives from.
            Assert.That(actual.Type, Is.EqualTo(damage == TierDamage.CatalogTierSix ? expected.Type.BaseType : expected.Type));
            Assert.That(expected.Message, Does.Contain(damage switch
            {
                TierDamage.MissingBlobFiles => "not registered with the blob manager",
                TierDamage.FormatByte => "Persisted snapshot format mismatch: on-disk v2, runtime expects v1.",
                TierDamage.CatalogTierOne => "incompatible or corrupted layout",
                _ => "Not a persisted tier.",
            }));
        }
    }

    /// <summary>The exception a start over a tier <paramref name="backend"/> wrote and <paramref name="damage"/> damaged fails with.</summary>
    private static async Task<(Type Type, string Message)> RefusedStartup(FlatDbBackend backend, TierDamage damage)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        await using (FlatManagerContainer first = new(StalledChain.Config(backend), path.Path, finality, overrides))
        {
            StalledChain.Add(first, finality, 0, 23);
        }

        Damage(path.Path, damage);
        Exception? refusal = Catch(() => new FlatManagerContainer(StalledChain.Config(backend), path.Path, finality, overrides));

        // The catalog opens again only once the refused start released it.
        Assert.That(() => FlatTestDbs.OpenRocksDbCatalog(path.Path).Dispose(), Throws.Nothing, "the catalog was let go");
        Assert.That(refusal, Is.Not.Null, $"{backend} refused to start");
        Exception cause = refusal!.GetBaseException();
        return (cause.GetType(), cause.Message);
    }

    private static void Damage(string basePath, TierDamage damage)
    {
        switch (damage)
        {
            case TierDamage.MissingBlobFiles:
                foreach (string file in Directory.EnumerateFiles(StalledChain.BlobDirectory(basePath))) File.Delete(file);
                break;
            case TierDamage.FormatByte:
                foreach (CatalogEntry row in StalledChain.Catalog(basePath))
                {
                    using FileStream arena = new(StalledChain.ArenaFile(basePath, row.Location.ArenaId), FileMode.Open, FileAccess.Write);
                    RandomAccess.Write(arena.SafeFileHandle, [2], row.Location.Offset + row.Location.Size - 1);
                }

                break;
            default:
                using (FlatTestDbs.TrackingCatalogDb catalog = FlatTestDbs.OpenRocksDbCatalog(basePath))
                {
                    foreach (KeyValuePair<byte[], byte[]> row in catalog.GetAll().ToArray())
                    {
                        byte[] value = row.Value;
                        value[^1] = damage == TierDamage.CatalogTierOne ? (byte)1 : (byte)6;
                        catalog.PutSpan(row.Key, value, WriteFlags.None);
                    }
                }

                break;
        }
    }

    /// <summary>Where a panic in the persisted-snapshot tier strikes.</summary>
    public enum TierPanicSite
    {
        Conversion,
        PersistedCompaction,
        TierRead,
        Load,
    }

    /// <summary>
    /// A panic converting a snapshot into the persisted-snapshot tier, merging the tier, reading it, or opening it at
    /// startup poisons the store, as any panic does: every later call fails fast, disposal still releases everything,
    /// and the C# backend then opens the persisted state and the tier as they were before the panic.
    /// </summary>
    [Test]
    public async Task A_panic_in_the_persisted_snapshot_tier_poisons_the_store([Values] TierPanicSite site)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        FdbStatus?[] later = [];
        Exception? failure;
        StateId persistedBefore;
        if (site == TierPanicSite.Load)
        {
            await using (FlatManagerContainer first = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, overrides))
            {
                StalledChain.Add(first, finality, 0, 23);
                persistedBefore = first.PersistenceManager.GetCurrentPersistedStateId();
            }

            FlatTestDbs.TrackingColumnsDb db = new(Path.Combine(path.Path, DbNames.Flat));
            failure = Catch(() => new FlatManagerContainer(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality,
                overrides with { Db = db, StoreOpened = static store => NativeFaults.Inject(store, NativeFaults.Fault.PanicInPersistedLoad, "injected") }));
            Assert.That(db.NativeCloses, Is.EqualTo(1), "the refused start released the database");
        }
        else
        {
            FlatManagerContainer container = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, overrides);
            StalledChain.Add(container, finality, 0, site == TierPanicSite.TierRead ? 23UL : 4UL);
            persistedBefore = container.PersistenceManager.GetCurrentPersistedStateId();
            NativeFaults.Inject(container.Resolve<RustPersistence>().Store, site switch
            {
                TierPanicSite.Conversion => NativeFaults.Fault.PanicInConvert,
                TierPanicSite.PersistedCompaction => NativeFaults.Fault.PanicInPersistedCompaction,
                _ => NativeFaults.Fault.PanicInTierRead,
            }, "injected");
            if (site == TierPanicSite.TierRead)
            {
                using IBundleView tierOnly = container.Arm.GatherReadOnly(Block(6), ReaderFlags.None);
                failure = Catch(() => tierOnly.GetAccount(BlockAddress));
            }
            else
            {
                failure = Catch(() => StalledChain.Add(container, finality, 5, 31));
            }

            Assert.That(() => Status(() => container.Arm.HasStateForBlock(Block(0))), Is.EqualTo(FdbStatus.Poisoned).After(EventuallyMs, PollMs));
            later =
            [
                Status(() => container.AddBlock(32)),
                Status(() => container.Arm.GatherReadOnly(Block(4), ReaderFlags.None).Dispose()),
                Status(() => container.Arm.FlushCache(CancellationToken.None)),
                Status(() => container.PersistenceManager.GetCurrentPersistedStateId()),
            ];
            Exception? disposalFailure = await CatchAsync(async () => await container.DisposeAsync());
            Assert.That((disposalFailure, container.Db.NativeCloses), Is.EqualTo(((Exception?)null, 1)));
        }

        await using FlatManagerContainer reopened = new(StalledChain.Config(FlatDbBackend.CSharp), path.Path, finality, overrides);
        StateId persisted = reopened.PersistenceManager.GetCurrentPersistedStateId();
        using (Assert.EnterMultipleScope())
        {
            if (site is TierPanicSite.TierRead or TierPanicSite.Load) Assert.That((failure?.GetBaseException() as RustFlatDbException)?.Status, Is.EqualTo(FdbStatus.Panic));
            Assert.That(later, Is.All.EqualTo(FdbStatus.Poisoned));
            Assert.That(persisted, Is.EqualTo(persistedBefore), "the panicking store persisted nothing more");
            foreach (ulong number in StalledChain.Held(reopened, 31).Where(number => number > persisted.BlockNumber))
            {
                Assert.That(StalledChain.Reads(reopened, Block(number)), Is.EqualTo(StalledChain.ExpectedReads(number)), $"reads {number}");
            }
        }
    }

    /// <summary>Which database a fatal error of a persist or a conversion with long finality hits.</summary>
    public enum FatalTarget
    {
        /// <summary>The flat DB, written by a persist out of the tier.</summary>
        Columns,

        /// <summary>The persisted-snapshot catalog, written by a conversion into the tier.</summary>
        Catalog,
    }

    /// <summary>
    /// With long finality, a fatal RocksDB error shuts down through <see cref="DbOnTheRocks.HandleFatalDbError"/> of the
    /// database it hit, the flat DB for a persist and the catalog for a conversion, and freezes the persisted state: a
    /// flush then fails with it.
    /// </summary>
    [Test]
    public async Task A_fatal_error_shuts_down_through_the_database_it_hit([Values] FatalTarget target)
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        await using FlatManagerContainer container = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, new() { RocksDbCatalog = true });
        StalledChain.Add(container, finality, 0, 15);
        StateId persisted = container.PersistenceManager.GetCurrentPersistedStateId();
        FlatTestDbs.TrackingCatalogDb catalog = (FlatTestDbs.TrackingCatalogDb)container.CatalogDb;

        NativeFaults.Inject(container.Resolve<RustPersistence>().Store, target == FatalTarget.Columns ? NativeFaults.Fault.KvWrite : NativeFaults.Fault.CatalogPutError, "IO error: injected");
        // Finality moving on makes the next persists write the flat DB; while it stalls they convert into the tier.
        if (target == FatalTarget.Columns) finality.Finalize(10, Block);
        Catch(() => StalledChain.Add(container, finality, 16, 19));
        Func<int> hit = target == FatalTarget.Columns ? () => container.Db.FatalShutdowns : () => catalog.FatalShutdowns;
        Assert.That(hit, Is.Positive.After(EventuallyMs, PollMs), "the error reached the database it hit");
        Exception? flushFailure = Catch(() => container.Arm.FlushCache(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flushFailure, Is.InstanceOf<RocksDbException>().With.Message.EqualTo("IO error: injected"));
            Assert.That(target == FatalTarget.Columns ? catalog.FatalShutdowns : container.Db.FatalShutdowns, Is.Zero, "the other database");
            Assert.That(container.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(persisted));
        }

        if (target == FatalTarget.Catalog)
        {
            // A flat DB error the manager fails a call with after the catalog's error is still the flat DB's.
            RustPersistence persistence = container.Resolve<RustPersistence>();
            NativeFaults.Inject(persistence.Store, NativeFaults.Fault.KvFlush, "IO error: flat");
            Catch(persistence.Flush);
            Exception? flatFailure = Catch(() => container.Resolve<Fork.NativeFlatManager>().ThrowIfFailed(FdbStatus.RocksDb));
            Assert.That((flatFailure?.GetType(), flatFailure?.Message, container.Db.FatalShutdowns), Is.EqualTo((typeof(RocksDbException), "IO error: flat", 2)),
                "the flush and the manager reported it to the flat DB");
        }
    }

    /// <summary>
    /// With <see cref="IFlatDbConfig.ValidatePersistedSnapshot"/>, a converted snapshot that does not read back as its
    /// source is dumped, logged as the C# loader logs it, and exits the process with <see cref="ExitCodes.GeneralError"/>.
    /// </summary>
    [Test]
    public async Task A_converted_snapshot_failing_validation_is_dumped_and_exits_the_process()
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        RecordingLogManager logManager = new(LogLevel.Error);
        FlatDbConfig config = StalledChain.Config(FlatDbBackend.Rust);
        config.ValidatePersistedSnapshot = true;
        await using FlatManagerContainer container = new(config, path.Path, finality, new() { RocksDbCatalog = true, LogManager = logManager });
        TaskCompletionSource<int> exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        container.Resolve<Fork.NativeFlatManager>().ExitProcess = code => exitCode.TrySetResult(code);
        StalledChain.Add(container, finality, 0, 4);

        NativeFaults.Inject(container.Resolve<RustPersistence>().Store, NativeFaults.Fault.CorruptConvertedContent, "injected");
        Exception? failure = Catch(() => StalledChain.Add(container, finality, 5, 15));
        bool exited = exitCode.Task.Wait(EventuallyMs);
        string? logged = logManager.Lines.FirstOrDefault(static line => line.Contains("Persisted snapshot validation failed"));
        Match range = Regex.Match(logged ?? "", @"for range (\d+)\.\.(\d+)\.");
        string dump = $"broken.{range.Groups[1].Value}.{range.Groups[2].Value}.json";
        bool dumped = File.Exists(dump);
        if (dumped) File.Delete(dump);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<InvalidOperationException>().With.Message.Contains($"Dumped snapshot to {dump}"));
            Assert.That(exited, Is.True, "the process exit was requested");
            Assert.That(exitCode.Task.IsCompletedSuccessfully ? exitCode.Task.Result : -1, Is.EqualTo(ExitCodes.GeneralError));
            Assert.That(logged, Does.Contain($"Exiting with code {ExitCodes.GeneralError} for git bisect compatibility."));
            Assert.That(range.Success && dumped, Is.True, $"{dump} was written");
        }
    }

    /// <summary>
    /// A crash after a conversion made its files durable and before it wrote its catalog row leaves orphaned bytes the
    /// next start ignores: it truncates no file, reads the catalogued snapshots exactly, and writes the next tables to
    /// new arena files, past the orphans.
    /// </summary>
    [Test]
    public async Task Orphans_of_a_crash_before_the_catalog_row_are_ignored_by_the_next_start()
    {
        NativeFaults.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        RecordingLogManager logManager = new(LogLevel.Error);
        Exception? failure;
        await using (FlatManagerContainer first = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, overrides with { LogManager = logManager }))
        {
            StalledChain.Add(first, finality, 0, 15);
            NativeFaults.Inject(first.Resolve<RustPersistence>().Store, NativeFaults.Fault.SkipCatalogAdd, "injected");
            // The conversion the fault stops runs on this thread or on the persistence worker, which logs its failure;
            // the next one converts the same bases again.
            failure = Catch(() => StalledChain.Add(first, finality, 16, 23));
        }

        Dictionary<string, long> lengthsAtShutdown = FileLengths(path.Path);
        CatalogEntry[] bases = [.. StalledChain.Catalog(path.Path).Where(static row => row.Tier == SnapshotTier.PersistedBase)];
        await using FlatManagerContainer reopened = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, overrides);
        Dictionary<string, long> lengthsAtStart = FileLengths(path.Path);
        int[] idsAtStart = StalledChain.ArenaIds(path.Path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(logManager.Lines.Append(failure?.Message ?? ""), Has.Some.Contains("stopped before its catalog row"), "a conversion stopped");
            Assert.That(lengthsAtStart, Is.SubsetOf(lengthsAtShutdown), "no file was truncated at the start");
            Assert.That(lengthsAtStart.Keys.Where(static file => file.StartsWith("arena")), Is.EquivalentTo(lengthsAtShutdown.Keys.Where(static file => file.StartsWith("arena"))), "no arena file was deleted at the start");
            foreach (CatalogEntry row in bases)
            {
                Assert.That(StalledChain.Reads(reopened, row.To), Is.EqualTo(StalledChain.ExpectedReads(row.To.BlockNumber)), $"reads {row.To}");
            }
        }

        ulong resume = StalledChain.Held(reopened, 23)[^1];
        StalledChain.Add(reopened, finality, resume + 1, resume + 12);
        Dictionary<string, long> lengthsAfter = FileLengths(path.Path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lengthsAtStart.Where(static file => file.Key.StartsWith("arena")), Is.SubsetOf(lengthsAfter), "the arena files of the previous run were not written again");
            Assert.That(StalledChain.ArenaIds(path.Path).Except(idsAtStart), Is.Not.Empty.And.All.GreaterThan(idsAtStart[^1]), "the next tables went to new arena files");
            foreach (ulong number in StalledChain.Held(reopened, resume + 12).Where(number => number > StalledChain.FinalizedBlock))
            {
                Assert.That(StalledChain.Reads(reopened, Block(number)), Is.EqualTo(StalledChain.ExpectedReads(number)), $"reads {number}");
            }
        }

        static Dictionary<string, long> FileLengths(string basePath)
        {
            string tier = Path.GetDirectoryName(StalledChain.ArenaDirectory(basePath))!;
            return Directory.EnumerateFiles(tier, "*.bin", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(tier, file), static file => new FileInfo(file).Length);
        }
    }

    /// <summary>The status of the native failure <paramref name="call"/> throws, null when it throws none.</summary>
    private static FdbStatus? Status(Action call) => (Catch(call) as RustFlatDbException)?.Status;

    private static Exception? Catch(Action call)
    {
        try
        {
            call();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CatchAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
