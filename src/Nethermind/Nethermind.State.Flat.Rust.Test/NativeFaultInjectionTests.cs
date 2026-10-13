// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using Nethermind.State.Flat.Persistence;
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
