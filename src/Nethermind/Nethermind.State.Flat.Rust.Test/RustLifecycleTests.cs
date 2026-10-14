// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// How the Rust backends shut down and let go of the native library: with a persist in flight and handles still open,
/// in the order the container disposes them, on process exit during a compactor stall, twice, and from the finalizers
/// of handles nobody disposed.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class RustLifecycleTests
{
    private static readonly ValueHash256 MaxKey = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Eventually = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shutdown waits for the persist in flight, which completes, and the database stays open for the bundle, reader
    /// and iterator still open, which keep reading, until the last of them is disposed.
    /// </summary>
    [Test]
    public async Task Shutdown_completes_the_persist_in_flight_and_closes_the_database_after_the_last_handle(
        [Values(FlatDbBackend.RustPersistence, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestPersistenceBarrier barrier = new();
        TestFinality finality = new();
        FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, finality, new() { PersistenceBarrier = barrier });
        for (ulong number = 0; number <= 3; number++) container.AddBlock(number);
        container.Arm.FlushCache(CancellationToken.None);

        // Block 4, the next boundary, is finalized, so the persistence worker persists it once the head reaches block 6.
        finality.Finalize(4, Block);
        barrier.Hold();
        for (ulong number = 4; number <= 8; number++) container.AddBlock(number);
        bool persistHeld = barrier.WaitHeld(Eventually);
        IBundleView bundle = container.Arm.GatherReadOnly(Block(8), ReaderFlags.None);
        IPersistence.IPersistenceReader reader = container.PersistenceManager.LeaseReader();
        IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(ValueKeccak.Zero, MaxKey);

        Task disposal = Task.Run(async () => await container.DisposeAsync());
        bool disposedWithPersistHeld = disposal.Wait(Briefly);
        barrier.Release();
        bool disposedWithHandlesOpen = disposal.Wait(Briefly);
        int closesWithHandlesOpen = container.Db.NativeCloses;
        (Account?, Account?, bool) reads = (bundle.GetAccount(BlockAddress), reader.GetAccount(BlockAddress), iterator.MoveNext());
        iterator.Dispose();
        reader.Dispose();
        bundle.Dispose();
        bool disposed = disposal.Wait(Eventually);

        using IColumnsDb<FlatDbColumns> reopened = FlatTestDbs.OpenRocksDb(Path.Combine(path.Path, DbNames.Flat));
        using IPersistence.IPersistenceReader persisted = new RocksDbPersistence(reopened, LimboLogs.Instance, new FlatDbConfig()).CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistHeld, Is.True, "a persist waits in the barrier");
            Assert.That(disposedWithPersistHeld, Is.False, "shutdown waits for the persist in flight");
            Assert.That(disposedWithHandlesOpen, Is.False, "the open handles hold the database");
            Assert.That(closesWithHandlesOpen, Is.Zero);
            Assert.That(reads, Is.EqualTo((new Account(8, 8), new Account(3, 3), true)), "the handles read after the manager is gone");
            Assert.That(disposed, Is.True, "the last handle let the database go");
            Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
            Assert.That(persisted.CurrentState, Is.EqualTo(Block(4)), "the persist in flight was written");
        }
    }

    /// <summary>
    /// The container releases the native manager, then the store, before it disposes the database, which therefore
    /// has no lease to wait for.
    /// </summary>
    [Test]
    public async Task Container_releases_the_manager_and_the_store_before_the_database()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        RecordingLogManager logManager = new(LogLevel.Warn);
        FlatTestDbs.TrackingColumnsDb db = new(Path.Combine(path.Path, DbNames.Flat), TimeSpan.Zero, logManager);
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality(), new() { Db = db });
        Fork.NativeFlatManager native = container.Resolve<Fork.NativeFlatManager>();
        RustFlatStore store = container.Resolve<RustPersistence>().Store;
        (bool Manager, bool Store)? releasedAtClose = null;
        db.NativeClosing = () => releasedAtClose = (native.Handle.IsClosed, store.IsClosed);
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        container.Arm.GatherReadOnly(Block(2), ReaderFlags.None).Dispose();

        await container.DisposeAsync();

        Assert.That(releasedAtClose, Is.EqualTo((true, true)));
        Assert.That(logManager.Lines, Has.None.Contains("pinned slices are released"), "the database waited for no lease");
    }

    /// <summary>
    /// A block waiting for room in the compaction queue, behind a persist the barrier holds, gives up once process exit
    /// is signalled, as with the C# manager; the manager shuts down once the persist is let go.
    /// </summary>
    [Test]
    public async Task Process_exit_ends_a_compactor_stall([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using CancellationTokenSource exit = new();
        TestPersistenceBarrier barrier = new();
        TestFinality finality = new();
        finality.Finalize(64, Block);
        FlatDbConfig config = FlatManagerContainer.Config(backend);
        config.InlineCompaction = false;
        config.MaxInFlightCompactJob = 1;
        FlatManagerContainer container = new(config, path.Path, finality, new() { ProcessExitSource = new ProcessExitSource(exit.Token), PersistenceBarrier = barrier });
        barrier.Hold();

        // The persist waits in the barrier, a job waits for it in the persistence queue, the compactor waits for room
        // there, a job waits for the compactor, and the next block waits for room in the compaction queue.
        Task? stalled = null;
        for (ulong number = 0; number < 32 && stalled is null; number++)
        {
            ulong block = number;
            Task add = Task.Run(() => container.AddBlock(block));
            if (!add.Wait(TimeSpan.FromSeconds(2))) stalled = add;
        }

        await exit.CancelAsync();
        bool ended = stalled is not null && await Task.WhenAny(stalled, Task.Delay(Eventually)) == stalled;
        barrier.Release();
        Task disposal = container.DisposeAsync().AsTask();
        bool disposed = await Task.WhenAny(disposal, Task.Delay(Eventually)) == disposal;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stalled, Is.Not.Null, "a block stalled");
            Assert.That(ended, Is.True, "process exit ended the stall");
            Assert.That(stalled?.Exception?.GetBaseException(), Is.InstanceOf<OperationCanceledException>());
            Assert.That(disposed, Is.True);
        }
    }

    /// <summary>
    /// Process exit signalled while the persisted-snapshot tier converts a long stretch of stalled blocks lets the
    /// persist, at most cancelled, and the shutdown end, as with the C# manager, and leaves a tier the next start reads
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Whether the exit stops the conversions is not observable here: the background workers convert the backlog as
    /// it builds up, in milliseconds, and nothing holds a conversion on both backends. The cargo test
    /// <c>process_exit_cancels_a_conversion</c> pins that.
    /// </remarks>
    [Test]
    public async Task Process_exit_while_a_backlog_converts_lets_the_persist_and_the_shutdown_end([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        using CancellationTokenSource exit = new();
        TestFinality finality = new();
        finality.Finalize(StalledChain.FinalizedBlock, Block);
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        FlatManagerContainer container = new(StalledChain.Config(backend), path.Path, finality, overrides with { ProcessExitSource = new ProcessExitSource(exit.Token) });
        for (ulong number = 0; number < 44; number++) StalledChain.AddBlock(container, number);

        Task persisting = Task.Run(() => container.PersistUpTo(Block(43)));
        await exit.CancelAsync();
        bool ended = await Task.WhenAny(persisting, Task.Delay(Eventually)) == persisting;
        Task disposal = container.DisposeAsync().AsTask();
        bool disposed = await Task.WhenAny(disposal, Task.Delay(Eventually)) == disposal;

        await using FlatManagerContainer reopened = new(StalledChain.Config(backend), path.Path, finality, overrides);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((ended, disposed), Is.EqualTo((true, true)), "the persist and the shutdown ended");
            Assert.That(persisting.Exception?.GetBaseException(), Is.Null.Or.InstanceOf<OperationCanceledException>());
            foreach (ulong number in StalledChain.Held(reopened, 43).Where(static number => number > StalledChain.FinalizedBlock))
            {
                Assert.That(StalledChain.Reads(reopened, Block(number)), Is.EqualTo(StalledChain.ExpectedReads(number)), $"reads {number}");
            }
        }
    }

    /// <summary>
    /// Shutdown, with conversions into the persisted-snapshot tier and merges of it still in flight, keeps the files of
    /// every snapshot the tier holds, so the next start reads each catalogued snapshot exactly.
    /// </summary>
    [Test]
    public async Task Shutdown_keeps_the_files_of_the_persisted_snapshot_tier([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        await using (FlatManagerContainer first = new(StalledChain.Config(backend), path.Path, finality, overrides))
        {
            StalledChain.Add(first, finality, 0, 31);
        }

        CatalogEntry[] rows = StalledChain.Catalog(path.Path);
        await using FlatManagerContainer reopened = new(StalledChain.Config(backend), path.Path, finality, overrides);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(static row => row.Tier), Is.SupersetOf(new[] { SnapshotTier.PersistedBase, SnapshotTier.PersistedCompactSized }), "the tier held bases and merges");
            foreach (CatalogEntry row in rows)
            {
                long fileLength = Directory.EnumerateFiles(StalledChain.ArenaDirectory(path.Path), $"*_{row.Location.ArenaId:D4}.bin").Select(static file => new FileInfo(file).Length).DefaultIfEmpty(-1).Single();
                Assert.That(fileLength, Is.GreaterThanOrEqualTo(row.Location.Offset + row.Location.Size), $"the file of {row.Tier} {row.To}");
                Assert.That(StalledChain.Reads(reopened, row.To), Is.EqualTo(StalledChain.ExpectedReads(row.To.BlockNumber)), $"reads {row.Tier} {row.To}");
            }
        }
    }

    /// <summary>
    /// A read-only bundle over the persisted-snapshot tier, held across shutdown, keeps reading the tier, and holds the
    /// native manager, and with it the lease on the catalog, until it is disposed.
    /// </summary>
    [Test]
    public async Task A_bundle_held_across_shutdown_keeps_reading_the_tier_and_holds_the_catalog()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatManagerContainer container = new(StalledChain.Config(FlatDbBackend.Rust), path.Path, finality, new() { RocksDbCatalog = true });
        StalledChain.Add(container, finality, 0, 23);
        FlatTestDbs.TrackingCatalogDb catalog = (FlatTestDbs.TrackingCatalogDb)container.CatalogDb;
        FfiRobInfo info = NativeTier.GatherInfo(container.Resolve<Fork.NativeFlatManager>(), Block(6));
        IBundleView bundle = container.Arm.GatherReadOnly(Block(6), ReaderFlags.None);

        Task disposal = Task.Run(async () => await container.DisposeAsync());
        bool disposedWithBundleOpen = disposal.Wait(Briefly);
        int catalogClosesWithBundleOpen = catalog.NativeCloses;
        (Account? account, byte[]? node) = (bundle.GetAccount(BlockAddress), bundle.LoadStateRlp(StalledChain.NodePath));
        bundle.Dispose();
        bool disposed = disposal.Wait(Eventually);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(info.PersistedCount, Is.Positive.And.EqualTo(info.SnapshotCount), "the bundle reads the tier alone");
            Assert.That((disposedWithBundleOpen, catalogClosesWithBundleOpen), Is.EqualTo((false, 0)), "the open bundle holds the manager and the catalog");
            Assert.That(account, Is.EqualTo(new Account(6, 6)), "the bundle reads the tier after the manager is gone");
            Assert.That(node, Is.EqualTo(StalledChain.Rlp(6)));
            Assert.That(disposed, Is.True, "the bundle let go");
            Assert.That((catalog.NativeCloses, container.Db.NativeCloses), Is.EqualTo((1, 1)));
        }
    }

    /// <summary>Every handle, the persistence, the native manager, its shell and the container can be disposed twice.</summary>
    [Test]
    public async Task Disposing_twice_is_harmless([Values(FlatDbBackend.RustPersistence, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatManagerContainer container = new(FlatManagerContainer.Config(backend), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        RustPersistence persistence = container.Resolve<RustPersistence>();
        IDisposable[] native = backend == FlatDbBackend.Rust ? [container.Resolve<Fork.NativeFlatManager>()] : [];
        IAsyncDisposable[] shell = backend == FlatDbBackend.Rust ? [container.Resolve<Fork.RustFlatDbManager>()] : [];
        IPersistence.IPersistenceReader reader = persistence.CreateReader();
        IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(ValueKeccak.Zero, MaxKey);
        IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync);

        Assert.That(async () =>
        {
            foreach (IDisposable handle in new IDisposable[] { iterator, reader, batch })
            {
                handle.Dispose();
                handle.Dispose();
            }

            await container.DisposeAsync();
            await container.DisposeAsync();
            foreach (IAsyncDisposable disposable in shell) await disposable.DisposeAsync();
            foreach (IDisposable disposable in native) disposable.Dispose();
            persistence.Dispose();
        }, Throws.Nothing);
        Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
    }

    /// <summary>
    /// Handles nobody disposed, a reader, an iterator, a batch and a native manager, are released by their finalizers,
    /// which lets the store and the database go; the batch, never committed, writes nothing.
    /// </summary>
    [Test]
    public void Finalizers_release_abandoned_handles([Values] FlatTestDbs.Kind dbKind)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        IColumnsDb<FlatDbColumns> db = FlatTestDbs.Open(dbKind, path.Path);
        RustPersistence persistence = new(db, new FlatDbConfig(), LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, Block(1)))
        {
            batch.SetAccount(BlockAddress, new Account(1, 1));
        }

        RustFlatStore store = persistence.Store;
        Abandon(persistence);
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        persistence.Dispose();
        bool released = store.IsClosed;
        int bridgeHandles = store.Bridge?.OpenHandles ?? 0;
        StateId committed;
        using (RustPersistence reopened = new(db, new FlatDbConfig(), LimboLogs.Instance))
        using (IPersistence.IPersistenceReader reader = reopened.CreateReader())
        {
            committed = reader.CurrentState;
        }

        db.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.True, "the finalizers released every handle on the store");
            Assert.That(bridgeHandles, Is.Zero);
            Assert.That(committed, Is.EqualTo(Block(1)), "the abandoned batch wrote nothing");
            if (db is FlatTestDbs.TrackingColumnsDb rocksDb) Assert.That(rocksDb.NativeCloses, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Disposing the node shuts the native manager down, but a scope bundle and a snapshot it committed, both still
    /// open, keep working and keep the database until they are disposed; the database then closes once.
    /// </summary>
    [Test]
    public async Task Shutdown_waits_for_a_live_scope_and_its_snapshot_which_keep_working()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        Fork.SnapshotBundle scope = container.Resolve<Fork.IFlatDbManager>().GatherSnapshotBundle(Block(2), ResourcePool.Usage.MainBlockProcessing);
        scope.SetAccount(BlockAddress, new Account(9, 9));
        (Fork.Snapshot? snapshot, TransientResource? transient) = scope.CollectAndApplySnapshot(Block(2), Block(3));
        TestInternals.ReleaseLease(transient!);

        Task disposal = Task.Run(async () => await container.DisposeAsync());
        bool disposedWithScopeOpen = disposal.Wait(Briefly);
        scope.SetChangedSlot(BlockAddress, 1, 1);
        (Account? committed, bool snapshotAlive) = (scope.GetAccount(BlockAddress), snapshot!.TryAcquire());
        snapshot.Dispose();
        snapshot.Dispose();
        scope.Dispose();
        bool disposed = disposal.Wait(Eventually);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposedWithScopeOpen, Is.False, "the open scope holds the database");
            Assert.That((committed, snapshotAlive), Is.EqualTo((new Account(9, 9), true)), "the scope and its snapshot work after the manager is gone");
            Assert.That(disposed, Is.True);
            Assert.That(container.Db.NativeCloses, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The handles of a native read-only bundle, a native scope and a committed snapshot nobody disposed are released by
    /// their finalizers: the snapshot's content is freed, and the store and the database go.
    /// </summary>
    /// <remarks>
    /// The forked bundle classes hold a reference on their handle until they are disposed, so a handle is finalized
    /// only once nothing but the finalizer holds it.
    /// </remarks>
    [Test]
    public async Task Finalizers_release_abandoned_bundle_scope_and_snapshot_handles()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        for (ulong number = 0; number <= 2; number++) container.AddBlock(number);
        long contents = LiveMainContents(container);
        long contentsHeld = AbandonNative(container);
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        long contentsAfterFinalizers = LiveMainContents(container);
        Task disposal = container.DisposeAsync().AsTask();
        bool disposed = await Task.WhenAny(disposal, Task.Delay(Eventually)) == disposal;

        using (Assert.EnterMultipleScope())
        {
            Assert.That((contentsHeld, contentsAfterFinalizers), Is.EqualTo((contents + 1, contents)), "the abandoned snapshot held its content until finalized");
            Assert.That((disposed, container.Db.NativeCloses), Is.EqualTo((true, 1)));
        }
    }

    /// <summary>
    /// Opens a native read-only bundle and a native scope of the forked manager, and keeps a snapshot a scope committed,
    /// and drops all three handles.
    /// </summary>
    /// <returns>The live contents of block processing while the snapshot is held.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe long AbandonNative(FlatManagerContainer container)
    {
        using (Fork.SnapshotBundle scope = container.Resolve<Fork.IFlatDbManager>().GatherSnapshotBundle(Block(2), ResourcePool.Usage.MainBlockProcessing))
        {
            scope.SetAccount(BlockAddress, new Account(9, 9));
            (Fork.Snapshot? snapshot, TransientResource? transient) = scope.CollectAndApplySnapshot(Block(2), Block(3));
            TestInternals.ReleaseLease(transient!);
            GC.KeepAlive(snapshot);
        }

        long held = LiveMainContents(container);
        Fork.NativeFlatManager native = container.Resolve<Fork.NativeFlatManager>();
        RustFlatStore store = container.Resolve<RustPersistence>().Store;
        FfiStateId head = new(Block(2));
        FfiScopeFlags flags = new(filterInMemorySlotReads: false, ResourcePool.Usage.MainBlockProcessing);
        for (int handle = 0; handle < 2; handle++)
        {
            store.AddChildReference();
            bool managerAdded = false;
            native.Handle.DangerousAddRef(ref managerAdded);
            nint bundle;
            FfiRobInfo info;
            native.ThrowIfFailed(handle == 0
                ? NativeMethods.fdb_rob_gather(native.Handle, &head, 0, &bundle, &info)
                : NativeMethods.fdb_scope_open(native.Handle, &head, 0, &flags, &bundle, &info));
            _ = handle == 0 ? new RobHandle(store, native.Handle, bundle) : (BundleBaseHandle)new ScopeHandle(store, native.Handle, bundle);
        }

        return held;
    }

    private static unsafe long LiveMainContents(FlatManagerContainer container)
    {
        FdbCounters* counters = NativeMethods.fdb_stats(container.Resolve<Fork.NativeFlatManager>().Handle.DangerousGetHandle());
        return Volatile.Read(ref counters->ContentActive[(int)ResourcePool.Usage.MainBlockProcessing]);
    }

    /// <summary>Opens a reader, an iterator, a batch and a native manager on the store of <paramref name="persistence"/>, and drops them.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void Abandon(RustPersistence persistence)
    {
        IPersistence.IPersistenceReader reader = persistence.CreateReader();
        reader.CreateAccountIterator(ValueKeccak.Zero, MaxKey).MoveNext();
        persistence.CreateWriteBatch(Block(1), Block(2)).SetAccount(BlockAddress, new Account(2, 2));

        RustFlatStore store = persistence.Store;
        GCHandle host = GCHandle.Alloc(new RustHostCallbacks(new TestFinality(), NullStatePersistenceBarrier.Instance, LimboLogs.Instance));
        FdbHost callbacks = RustHostCallbacks.ToNative(host);
        FdbMgrCfg cfg = new()
        {
            AbiVersion = FfiAbi.Version,
            MaxInFlightCompactJob = 1,
            CompactSize = 4,
            PersistedSnapshotMaxCompactSize = 1024,
            MinReorgDepth = 2,
            MaxReorgDepth = 12,
            SecondsPerSlot = 12,
        };
        nint manager;
        store.AddChildReference();
        StatusMap.ThrowIfFailed(NativeMethods.fdb_mgr_create(store, &cfg, &callbacks, &manager));
        _ = new ManagerHandle(store, manager, host, null);
    }

}
