// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Migration;

[TestFixture]
public class HalfPathMigratorTests
{
    [Test]
    public async Task Copies_the_trie_as_the_importer_does([Values(1, 7, 1_000)] int chunkNodes, [Values] bool firstWriteRejected)
    {
        using MemDb halfPathDb = new();
        NodeStorage halfPath = new(halfPathDb);
        (Hash256 root, _) = HalfPathFallbackPersistenceTests.BuildState(halfPath);
        StateId state = new(1, root);
        using Migration migration = new(halfPath, state, chunkNodes, rejectedWrites: firstWriteRejected ? 1 : 0);

        await migration.Run();

        using SnapshotableMemColumnsDb<FlatDbColumns> importedDb = new();
        await new Importer(halfPath, new RocksDbPersistence(importedDb, LimboLogs.Instance, NullTrieNodeLog.Instance), LimboLogs.Instance).Copy(state);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(migration.Cursor.Current, Is.Null, "complete");
            Assert.That(BasePersistence.ReadMigrationCursor(migration.Db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>().Where(static column => column != FlatDbColumns.Metadata))
            {
                Assert.That(Contents(migration.Db, column), Is.EquivalentTo(Contents(importedDb, column)), column.ToString());
            }
        }
    }

    [Test]
    public async Task A_missing_HalfPath_node_stops_the_migration_before_it()
    {
        using MemDb halfPathDb = new();
        NodeStorage halfPath = new(halfPathDb);
        (Hash256 root, _) = HalfPathFallbackPersistenceTests.BuildState(halfPath);
        (Hash256? address, TreePath path, TrieNode missing) = HalfPathFallbackPersistenceTests.CollectNodes(halfPath, root)
            .Last(static entry => entry.Address is not null && !entry.Node.IsLeaf);
        halfPath.Set(address, path, missing.Keccak!.ValueHash256, null);
        using Migration migration = new(halfPath, new StateId(1, root), chunkNodes: 7, rejectedWrites: 0);

        await migration.Run();

        MigrationCursor? cursor = migration.Cursor.Current;
        Assert.That(cursor, Is.Not.Null, "stopped");
        Assert.That(cursor!.Value.IsCopied(address, path), Is.False, "not past the missing node");
    }

    private static Dictionary<string, string> Contents(IColumnsDb<FlatDbColumns> db, FlatDbColumns column) =>
        db.GetColumnDb(column).GetAll().ToDictionary(static entry => entry.Key.ToHexString(), static entry => entry.Value!.ToHexString());

    /// <summary>A migration of the HalfPath state at <paramref name="state"/> into an empty flat DB pointing at it, run inline.</summary>
    private sealed class Migration : IDisposable
    {
        private readonly Queue<Func<Task>> _scheduled = new();
        private readonly HalfPathMigrator _migrator;
        private int _rejectedWrites;

        public SnapshotableMemColumnsDb<FlatDbColumns> Db { get; } = new();
        public HalfPathMigrationCursor Cursor { get; }

        public Migration(INodeStorage halfPath, StateId state, int chunkNodes, int rejectedWrites)
        {
            IPersistence flat = new RocksDbPersistence(Db, LimboLogs.Instance, NullTrieNodeLog.Instance);
            flat.CreateWriteBatch(StateId.PreGenesis, state).Dispose();
            Cursor = new HalfPathMigrationCursor(Db);
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

            _migrator = new HalfPathMigrator(persistenceManager, Cursor, new FlatDbConfig { MigrationChunkNodes = chunkNodes }, new InlineScheduler(_scheduled),
                Substitute.For<IProcessExitSource>(), LimboLogs.Instance);
        }

        /// <summary>Runs the scheduled chunks until the migration completes or none has been scheduled for a while.</summary>
        public async Task Run()
        {
            _migrator.Start();
            Stopwatch idle = Stopwatch.StartNew();
            while (Cursor.Current is not null && idle.Elapsed < TimeSpan.FromSeconds(3))
            {
                Func<Task>? chunk;
                lock (_scheduled) _scheduled.TryDequeue(out chunk);
                if (chunk is not null)
                {
                    await chunk();
                    idle.Restart();
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class InlineScheduler(Queue<Func<Task>> scheduled) : IBackgroundTaskScheduler
    {
        public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
            where TReq : notnull, IBackgroundTaskRequest<TReq>
        {
            lock (scheduled) scheduled.Enqueue(() => fulfillFunc(request, CancellationToken.None));
            return true;
        }
    }
}
