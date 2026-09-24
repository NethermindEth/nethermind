// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
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
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
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
        Assert.That(advance, Is.GreaterThan(0), "the state pointer must advance, and not before a flush");
        Assert.That(log[advance - 1], Is.EqualTo("flush"), "the state pointer must advance straight after a flush");
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
        // Two ticks with nothing visited in between: each still writes a line
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
    public async Task Copy_StopsTheProgressTimerWhenTheImportFails()
    {
        ManualTimer timer = new();
        Importer importer = new(new NodeStorage(_trieDb), _persistence, new OneLoggerLogManager(new ILogger(CreateInfoLogger())), timer.TimeProvider);

        // Unknown state root, so the traversal fails on its first node
        await Assert.ThatAsync(async () => await importer.Copy(new StateId(0, TestItem.KeccakA)),
            Throws.InstanceOf<TrieException>());

        _ = timer.Timer.Received(1).DisposeAsync();
    }

    private static InterfaceLogger CreateInfoLogger()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
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
}
