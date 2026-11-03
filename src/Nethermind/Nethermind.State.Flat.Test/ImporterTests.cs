// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ImporterTests
{
    private MemDb _trieDb = null!;
    private StateTree _stateTree = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;
    private Importer _importer = null!;

    [SetUp]
    public void SetUp()
    {
        _trieDb = new MemDb();
        _stateTree = new StateTree(new RawScopedTrieStore(_trieDb), LimboLogs.Instance);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        _importer = new Importer(new NodeStorage(_trieDb), _persistence, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _trieDb.Dispose();
        _columnsDb.Dispose();
    }

    private (Address address, Account account)[] SeedTree(int count)
    {
        (Address, Account)[] accounts = new (Address, Account)[count];
        for (int i = 0; i < count; i++)
        {
            (Address addr, Account acc) = (TestItem.GetRandomAddress(), TestItem.GenerateIndexedAccount(i + 1));
            accounts[i] = (addr, acc);
            _stateTree.Set(addr, acc);
        }
        _stateTree.Commit();
        return accounts;
    }

    [Test]
    public async Task Copy_TransfersAllAccountsFromTrieToFlatPersistence()
    {
        (Address address, Account account)[] accounts = SeedTree(10);

        await _importer.Copy(new StateId(0, _stateTree.RootHash));

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        foreach ((Address addr, Account expected) in accounts)
        {
            byte[]? rlp = reader.GetAccountRaw(new Hash256(addr.ToAccountPath.Bytes));
            Assert.That(rlp, Is.Not.Null, $"account {addr} should have been imported");
            RlpReader ctx = new(rlp!);
            Assert.That(AccountDecoder.Slim.Decode(ref ctx), Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Copy_AdvancesPersistenceCurrentStateToTarget()
    {
        SeedTree(3);
        StateId target = new(42, _stateTree.RootHash);

        await _importer.Copy(target);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(target));
    }

    [Test]
    public async Task Copy_FlushesDataBeforeAdvancingTheStatePointer()
    {
        // The ingest batches skip the WAL; a pointer that became durable first would survive a crash the data did not.
        SeedTree(3);
        List<string> log = [];
        Importer importer = new(new NodeStorage(_trieDb), new OrderRecordingPersistence(_persistence, log), LimboLogs.Instance);

        await importer.Copy(new StateId(42, _stateTree.RootHash));

        // Straight after, not merely after: a periodic ingest flush would satisfy the weaker check on a larger seed.
        int advance = log.IndexOf("advance-pointer");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(advance, Is.GreaterThan(0), "the state pointer must advance, and not before a flush");
            Assert.That(log[advance - 1], Is.EqualTo("flush"), "the state pointer must advance straight after a flush");
        }
    }

    [Test]
    public async Task Copy_PropagatesCancellation()
    {
        SeedTree(3);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThatAsync(async () => await _importer.Copy(new StateId(0, _stateTree.RootHash), cts.Token),
            Throws.InstanceOf<System.OperationCanceledException>());
    }

    [Test]
    public async Task Copy_WritesAProgressLineOnEveryTick()
    {
        // 10 accounts keep the estimate below 100 %, so a full line after the traversal comes from the importer
        SeedTree(10);
        ManualTimer timer = new();
        InterfaceLogger logger = CreateInfoLogger();
        Importer importer = new(new NodeStorage(_trieDb), new OrderRecordingPersistence(_persistence, [], timer.Tick),
            new OneLoggerLogManager(new ILogger(logger)), timer.TimeProvider);
        // Two back-to-back ticks: each writes a line
        int ticked = 0;
        logger.When(l => l.Info(Arg.Is<string>(line => line.Contains("Ingest thread started")))).Do(_ =>
        {
            if (Interlocked.Exchange(ref ticked, 1) == 0)
            {
                timer.Tick();
                timer.Tick();
            }
        });

        await importer.Copy(new StateId(0, _stateTree.RootHash));

        timer.TimeProvider.Received(1).CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        logger.Received(1).Warn(Arg.Is<string>(line => line.Contains("flat DB import") && line.Contains("every 60 s")));
        logger.Received(4).Info(Arg.Is<string>(line => line.Contains("Flat Import")));
        Received.InOrder(() =>
        {
            logger.Info(Arg.Is<string>(line => line.Contains("Flat Import") && line.Contains("written:") && line.Contains("queued:")));
            logger.Info(Arg.Is<string>(line => line.Contains("Flat Import") && line.Contains("written:") && line.Contains("queued:")));
            // One tick per final flush, after the traversal
            logger.Info(Arg.Is<string>(line => line.Contains("Flat Import") && line.Contains("100.00 %")));
            logger.Info(Arg.Is<string>(line => line.Contains("Flat Import") && line.Contains("100.00 %")));
            _ = timer.Timer.DisposeAsync();
            logger.Info(Arg.Is<string>(line => line.StartsWith("Flat db copy completed")));
        });
    }

    [Test]
    public async Task Copy_DoesNotReportFullProgressAfterACancelledTraversal()
    {
        // Fixed addresses give depth-1 leaves, so the tracker writes a Debug line mid-traversal
        _stateTree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(1));
        _stateTree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(2));
        _stateTree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(3));
        _stateTree.Commit();
        using CancellationTokenSource cts = new();
        ManualTimer timer = new();
        InterfaceLogger logger = CreateInfoLogger();
        logger.IsDebug.Returns(true);
        logger.When(l => l.Debug(Arg.Is<string>(line => line.Contains("Flat Import")))).Do(_ => cts.Cancel());
        Importer importer = new(new NodeStorage(_trieDb), _persistence, new OneLoggerLogManager(new ILogger(logger)), timer.TimeProvider);

        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, _stateTree.RootHash), cts.Token),
            Throws.InstanceOf<System.OperationCanceledException>());

        // The cancelled traversal returned normally, so only the token tells it from a completed one
        timer.Tick();
        logger.DidNotReceive().Info(Arg.Is<string>(line => line.Contains("100.00 %")));
    }

    [Test]
    public async Task Copy_StopsTheProgressTimerWhenTheImportFails()
    {
        ManualTimer timer = new();
        Importer importer = new(new NodeStorage(_trieDb), _persistence, new OneLoggerLogManager(new ILogger(CreateInfoLogger())), timer.TimeProvider);

        // Unknown state root, so the traversal fails on its first node
        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, TestItem.KeccakA)),
            Throws.InstanceOf<TrieException>());

        _ = timer.Timer.Received(1).DisposeAsync();
    }

    public enum Copied { Nothing, FirstAccountWithoutSlots, MidStorage, Everything }

    [Test]
    public void Migration_reads_match_the_trie_however_much_is_copied([Values] Copied copied)
    {
        NodeStorage halfPath = new(_trieDb);
        (Hash256 root, TestAccount[] accounts) = BuildState(halfPath);
        MigrationCursor cursor = copied switch
        {
            Copied.Nothing => MigrationCursor.Start,
            Copied.FirstAccountWithoutSlots => new MigrationCursor(accounts[0].Path, null),
            Copied.MidStorage => new MigrationCursor(accounts[AccountCount / 2].Path, accounts[AccountCount / 2].Slots[SlotsPerAccount / 2].Path),
            _ => new MigrationCursor(accounts[^1].Path, accounts[^1].Slots[^1].Path),
        };

        // Flat holds exactly what a migration at the cursor has copied.
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, root)))
        {
            foreach ((Hash256? address, TreePath path, TrieNode node) in CollectNodes(halfPath, root))
            {
                if (cursor.IsCopied(address, path)) Importer.WriteNode(batch, address, path, node);
            }
        }

        List<(Hash256? Address, TreePath Path)> halfPathReads = [];
        INodeStorage halfPathSpy = Substitute.For<INodeStorage>();
        halfPathSpy.Get(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>()).Returns(call =>
        {
            halfPathReads.Add((call.ArgAt<Hash256?>(0), call.ArgAt<TreePath>(1)));
            return halfPath.Get(call.ArgAt<Hash256?>(0), call.ArgAt<TreePath>(1), call.ArgAt<ValueHash256>(2), call.ArgAt<ReadFlags>(3));
        });
        HalfPathMigrationCursor migrationCursor = new(_columnsDb);
        migrationCursor.Advance(cursor);
        long walksBefore = Metrics.HalfPathFallbackWalks;

        using IPersistence.IPersistenceReader reader = new HalfPathFallbackPersistence(_persistence, halfPathSpy, migrationCursor).CreateReader();

        using (Assert.EnterMultipleScope())
        {
            foreach (TestAccount account in accounts)
            {
                Assert.That(reader.GetAccount(account.Address), Is.EqualTo(account.Account), account.Address.ToString());
                foreach (TestSlot slot in account.Slots)
                {
                    UInt256 value = 0;
                    Assert.That(reader.TryGetSlot(account.Address, slot.Index, ref value), Is.True, $"{account.Address} {slot.Index}");
                    Assert.That(value, Is.EqualTo(slot.Value), $"{account.Address} {slot.Index}");
                }
            }

            UInt256 missingValue = 0;
            Assert.That(reader.GetAccount(TestItem.GetRandomAddress()), Is.Null, "missing account");
            Assert.That(reader.TryGetSlot(accounts[^1].Address, SlotsPerAccount + 1, ref missingValue), Is.False, "missing slot");
            Assert.That(halfPathReads, Has.None.Matches<(Hash256? Address, TreePath Path)>(read => cursor.IsCopied(read.Address, read.Path)),
                "what the cursor has passed comes from flat alone");
            Assert.That(Metrics.HalfPathFallbackWalks > walksBefore, Is.EqualTo(copied != Copied.Everything), "only uncopied reads walk the trie");
            Assert.That(new HalfPathMigrationCursor(_columnsDb).Current, Is.EqualTo(cursor), "the cursor survives a restart");
        }
    }

    [Test]
    public async Task Migration_reads_an_account_or_storage_deleted_after_it_started_as_missing()
    {
        // HalfPath keeps the state the migration started from; flat has moved on to one without the first account and
        // with the second account's storage destroyed.
        NodeStorage halfPath = new(_trieDb);
        (_, TestAccount[] accounts) = BuildState(halfPath);
        TestAccount deleted = accounts[0];
        TestAccount destroyed = accounts[1];

        using MemDb laterDb = new();
        StateTree later = new(new RawScopedTrieStore(new NodeStorage(laterDb)), LimboLogs.Instance);
        later.Set(destroyed.Address, destroyed.Account.WithChangedStorageRoot(Keccak.EmptyTreeHash));
        later.Commit();

        await new Importer(new NodeStorage(laterDb), _persistence, LimboLogs.Instance).Copy(new StateId(2, later.RootHash));
        HalfPathMigrationCursor migrationCursor = new(_columnsDb);
        migrationCursor.Advance(MigrationCursor.Start);

        HalfPathFallbackPersistence persistence = new(_persistence, halfPath, migrationCursor);
        UInt256 value = 0;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(deleted.Address), Is.Null);
                Assert.That(reader.TryGetSlot(destroyed.Address, destroyed.Slots[0].Index, ref value), Is.False);
            }
        }

        persistence.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(migrationCursor.Current, Is.Null, "a wipe ends the migration");
            Assert.That(BasePersistence.ReadMigrationCursor(_columnsDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        }
    }

    [Test]
    public async Task Migration_copies_the_trie_as_the_importer_does([Values(1, 7, 1_000)] int chunkNodes, [Values] bool firstWriteRejected)
    {
        NodeStorage halfPath = new(_trieDb);
        (Hash256 root, _) = BuildState(halfPath);
        StateId state = new(1, root);
        MigrationRun migration = new(halfPath, _persistence, _columnsDb, state, chunkNodes, rejectedWrites: firstWriteRejected ? 1 : 0);
        long migratedBefore = Metrics.HalfPathMigratedNodes;

        await migration.Run();

        using SnapshotableMemColumnsDb<FlatDbColumns> importedDb = new();
        await new Importer(halfPath, new RocksDbPersistence(importedDb, LimboLogs.Instance, NullTrieNodeLog.Instance), LimboLogs.Instance).Copy(state);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(migration.Cursor.Current, Is.Null, "complete");
            Assert.That(Metrics.HalfPathMigratedNodes - migratedBefore, Is.EqualTo(CollectNodes(halfPath, root).Count),
                "every node is counted once, rejected chunks not at all");
            Assert.That(BasePersistence.ReadMigrationCursor(_columnsDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>().Where(static column => column != FlatDbColumns.Metadata))
            {
                Assert.That(Contents(_columnsDb, column), Is.EquivalentTo(Contents(importedDb, column)), column.ToString());
            }
        }
    }

    [Test]
    public async Task Migration_stops_before_a_missing_HalfPath_node()
    {
        NodeStorage halfPath = new(_trieDb);
        (Hash256 root, _) = BuildState(halfPath);
        (Hash256? address, TreePath path, TrieNode missing) = CollectNodes(halfPath, root)
            .Last(static entry => entry.Address is not null && !entry.Node.IsLeaf);
        halfPath.Set(address, path, missing.Keccak!.ValueHash256, null);
        MigrationRun migration = new(halfPath, _persistence, _columnsDb, new StateId(1, root), chunkNodes: 7, rejectedWrites: 0);

        await migration.Run();

        MigrationCursor? cursor = migration.Cursor.Current;
        Assert.That(cursor, Is.Not.Null, "stopped");
        Assert.That(cursor!.Value.IsCopied(address, path), Is.False, "not past the missing node");
    }

    private static InterfaceLogger CreateInfoLogger()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.IsWarn.Returns(true);
        return logger;
    }

    // Captures the importer's timer callback so a test can fire it
    private sealed class ManualTimer
    {
        private readonly TaskCompletionSource<TimerCallback> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualTimer() =>
            TimeProvider.CreateTimer(Arg.Do<TimerCallback>(c => _callback.TrySetResult(c)), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>())
                .Returns(Timer);

        public TimeProvider TimeProvider { get; } = Substitute.For<TimeProvider>();

        public ITimer Timer { get; } = Substitute.For<ITimer>();

        // The timer is created after the ingest threads start, so a tick from one of them waits for it
        public void Tick()
        {
            if (!_callback.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The importer did not create its progress timer");
            _callback.Task.Result(null);
        }
    }

    private sealed class OrderRecordingPersistence(IPersistence inner, List<string> log, Action? onFlush = null) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        {
            // Ingest batches write from the initial state to itself; only the final batch moves the pointer.
            if (from != to) log.Add("advance-pointer");
            return inner.CreateWriteBatch(from, to, flags);
        }

        public void Flush()
        {
            log.Add("flush");
            onFlush?.Invoke();
            inner.Flush();
        }

        public void Clear() => inner.Clear();
    }

    private const int AccountCount = 32;
    private const int SlotsPerAccount = 8;

    private readonly record struct TestSlot(UInt256 Index, UInt256 Value, ValueHash256 Path);

    private readonly record struct TestAccount(Address Address, Account Account, ValueHash256 Path, TestSlot[] Slots);

    /// <summary>Builds a state of accounts with storage in <paramref name="nodeStorage"/>, the accounts and their slots in path order.</summary>
    private static (Hash256 Root, TestAccount[] Accounts) BuildState(INodeStorage nodeStorage)
    {
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), LimboLogs.Instance);
        TestAccount[] accounts = new TestAccount[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            Address address = TestItem.GetRandomAddress();
            ValueHash256 accountPath = address.ToAccountPath;
            StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, accountPath.ToCommitment()), LimboLogs.Instance);
            TestSlot[] slots = new TestSlot[SlotsPerAccount];
            for (int s = 0; s < SlotsPerAccount; s++)
            {
                UInt256 index = (UInt256)s;
                ValueHash256 slotPath = default;
                StorageTree.ComputeKeyWithLookup(index, ref slotPath);
                slots[s] = new TestSlot(index, (UInt256)(i * SlotsPerAccount + s + 1), slotPath);
                storageTree.Set(index, slots[s].Value.ToBigEndian().WithoutLeadingZeros());
            }
            storageTree.Commit();

            Account account = TestItem.GenerateIndexedAccount(i + 1).WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(address, account);
            accounts[i] = new TestAccount(address, account, accountPath, slots.OrderBy(static slot => slot.Path).ToArray());
        }
        stateTree.Commit();

        return (stateTree.RootHash, accounts.OrderBy(static account => account.Path).ToArray());
    }

    /// <summary>Every node of the trie at <paramref name="root"/>, storage tries included, in path order.</summary>
    private static List<(Hash256? Address, TreePath Path, TrieNode Node)> CollectNodes(INodeStorage nodeStorage, Hash256 root)
    {
        NodeCollector collector = new();
        new PatriciaTree(new RawTrieStore(nodeStorage), LimboLogs.Instance).Accept(collector, root, new VisitingOptions { MaxDegreeOfParallelism = 1 });
        return collector.Nodes;
    }

    private static Dictionary<string, string> Contents(IColumnsDb<FlatDbColumns> db, FlatDbColumns column) =>
        db.GetColumnDb(column).GetAll().ToDictionary(static entry => entry.Key.ToHexString(), static entry => entry.Value!.ToHexString());

    private sealed class NodeCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public List<(Hash256? Address, TreePath Path, TrieNode Node)> Nodes { get; } = [];
        public bool IsFullDbScan => true;
        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new InvalidOperationException($"Missing node {nodeHash} at {nodeContext.Path}");
        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }
    }

    /// <summary>A migration of the HalfPath state at <paramref name="state"/> into an empty flat DB pointing at it, run on the test thread.</summary>
    private sealed class MigrationRun
    {
        private readonly Queue<Func<Task>> _scheduled = new();
        private readonly Nethermind.Core.Timers.ITimer _retryTimer = Substitute.For<Nethermind.Core.Timers.ITimer>();
        private readonly HalfPathMigrator _migrator;
        private int _rejectedWrites;
        private bool _retryPending;

        public HalfPathMigrationCursor Cursor { get; }

        public MigrationRun(INodeStorage halfPath, IPersistence flat, IColumnsDb<FlatDbColumns> flatDb, StateId state, int chunkNodes, int rejectedWrites)
        {
            flat.CreateWriteBatch(StateId.PreGenesis, state).Dispose();
            Cursor = new HalfPathMigrationCursor(flatDb);
            Cursor.Advance(MigrationCursor.Start);
            IPersistence persistence = new HalfPathFallbackPersistence(flat, halfPath, Cursor);
            _rejectedWrites = rejectedWrites;

            // Rejects the first writes as a persist holding the lock or moving the state would.
            IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
            persistenceManager.LeaseReader(Arg.Any<ReaderFlags>()).Returns(call => persistence.CreateReader(call.Arg<ReaderFlags>()));
            persistenceManager.TryWriteOnPersistedState(Arg.Any<StateId>(), Arg.Any<Action<IPersistence.IWriteBatch>>()).Returns(call =>
            {
                if (_rejectedWrites-- > 0) return false;
                using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(state, state);
                call.Arg<Action<IPersistence.IWriteBatch>>()(batch);
                return true;
            });

            _retryTimer.When(static timer => timer.Start()).Do(_ => _retryPending = true);
            _migrator = new HalfPathMigrator(persistenceManager, Cursor, new FlatDbConfig { MigrationChunkNodes = chunkNodes }, new InlineScheduler(_scheduled),
                new FunctionalTimerFactory(_ => _retryTimer), Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        }

        /// <summary>Runs the scheduled chunks, firing the retry timer whenever it is due, until nothing is left to run.</summary>
        public async Task Run()
        {
            _migrator.Start();
            while (Cursor.Current is not null)
            {
                if (_scheduled.TryDequeue(out Func<Task>? chunk))
                {
                    await chunk();
                }
                else if (_retryPending)
                {
                    _retryPending = false;
                    _retryTimer.Elapsed += Raise.Event();
                }
                else
                {
                    return;
                }
            }
        }
    }

    private sealed class InlineScheduler(Queue<Func<Task>> scheduled) : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            scheduled.Enqueue(() => fulfillFunc(request, CancellationToken.None));
            return true;
        }
    }
}
