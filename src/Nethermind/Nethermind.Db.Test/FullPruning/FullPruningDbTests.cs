// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db.Rocks;
using Nethermind.Db.FullPruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class FullPruningDbTests
    {
        [Test]
        [NonParallelizable]
        public void Pruning_finished_notification_runs_after_cleanup_without_holding_start_lock()
        {
            using FullPruningDb db = new(new DbSettings(DbNames.State, "notification-state"), new MemDbFactory());
            Assert.That(db.TryStartPruning(out IPruningContext? previous), Is.True);
            System.Threading.Tasks.Task<IPruningContext>? next = null;
            bool completedWithinNotification = false;
            System.EventHandler<PruningEventArgs> handler = (_, _) =>
            {
                next = System.Threading.Tasks.Task.Run(() =>
                {
                    if (!db.TryStartPruning(out IPruningContext? context))
                        throw new System.InvalidOperationException("Old pruning context was not detached.");
                    context.MarkStart();
                    return context;
                });
                completedWithinNotification = next.Wait(System.TimeSpan.FromSeconds(5));
            };
            db.PruningFinished += handler;
            previous!.Dispose();
            db.PruningFinished -= handler;
            using IPruningContext current = next!.GetAwaiter().GetResult();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(completedWithinNotification, Is.True);
                Assert.That(Metrics.StateDbPruning, Is.EqualTo(1));
                Assert.That(db.CanStartPruning, Is.False);
            }
        }

        [Test]
        public void Published_pruning_database_survives_cleanup_and_notification_failure([Values] bool failNotification)
        {
            using ThrowingOldDbCleanup db = new();
            Assert.That(db.TryStartPruning(out IPruningContext? context), Is.True);
            context!.Set([1], [2]);
            Assert.Throws<System.IO.IOException>(context.Commit);

            using (IAtomicWriteBatch batch = db.StartAtomicWriteBatch())
            {
                batch.Set([3], [4], WriteFlags.DisableWAL);
                batch.Commit();
            }
            System.EventHandler<PruningEventArgs> notification = (_, _) => throw new System.IO.IOException("Notification failed.");
            if (failNotification) db.PruningFinished += notification;
            if (failNotification) Assert.Throws<System.IO.IOException>(context.Dispose);
            else context.Dispose();
            db.PruningFinished -= notification;
            context.Dispose();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(db.Get([1]), Is.EqualTo(new byte[] { 2 }));
                Assert.That(db.Get([3]), Is.EqualTo(new byte[] { 4 }));
                Assert.That(db.CanStartPruning, Is.True);
            }
            Assert.That(db.TryStartPruning(out IPruningContext? retry), Is.True);
            retry!.Dispose();
        }

        private sealed class ThrowingOldDbCleanup() : FullPruningDb(new DbSettings(DbNames.State, "atomic-cleanup"), new MemDbFactory())
        {
            protected override void ClearOldDb(IDb oldDb) => throw new System.IO.IOException("Old database cleanup failed.");
        }

        [Test]
        public void Atomic_writes_defer_or_invalidate_pruning([Values] bool pruningFirst, [Values] bool commit)
        {
            using FullPruningDb db = new(new DbSettings(DbNames.State, "atomic-state"), new MemDbFactory());
            AssertAtomicPruning(db, pruningFirst, commit);
        }

        [Test]
        public void Rocks_atomic_writes_defer_or_invalidate_pruning([Values] bool pruningFirst, [Values] bool commit)
        {
            using TempPath tempPath = TempPath.GetTempDirectory();
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(
                    new InitConfig { BaseDbPath = tempPath.Path },
                    new PruningConfig { Mode = PruningMode.Full },
                    new FlatDbConfig { Enabled = false }))
                .AddSingleton<IDbFactory, RocksDbFactory>()
                .Build();
            FullPruningDb db = (FullPruningDb)container.ResolveKeyed<IDb>(DbNames.State);
            AssertAtomicPruning(db, pruningFirst, commit);
        }

        private static void AssertAtomicPruning(FullPruningDb db, bool pruningFirst, bool commit)
        {
            db.Set([1], [2]);
            IPruningContext? context = null;
            bool? successfulPruning = null;
            db.PruningFinished += (_, args) => successfulPruning = args.Success;
            if (pruningFirst) Assert.That(db.TryStartPruning(out context), Is.True);
            using IPruningContext? ownedContext = context;

            using (IAtomicWriteBatch batch = db.StartAtomicWriteBatch())
            {
                batch.Set([1], [3], WriteFlags.DisableWAL);
                batch.Set([4], [5], WriteFlags.DisableWAL);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(db.CanStartPruning, Is.False);
                    Assert.That(db.TryStartPruning(out _), Is.False);
                }
                if (pruningFirst)
                {
                    Assert.That(context!.Superseded.IsCancellationRequested, Is.True);
                    Assert.Throws<System.InvalidOperationException>(() => context.Commit());
                }
                if (commit) batch.Commit();
            }

            if (pruningFirst)
            {
                Assert.Throws<System.InvalidOperationException>(() => context!.Commit());
                context!.Dispose();
                Assert.That(successfulPruning, Is.False);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(db.Get([1]), Is.EqualTo(new byte[] { commit ? (byte)3 : (byte)2 }));
                Assert.That(db.Get([4]), commit ? Is.EqualTo(new byte[] { 5 }) : Is.Null);
                Assert.That(db.CanStartPruning, Is.True);
            }
            Assert.That(db.TryStartPruning(out IPruningContext? retry), Is.True);
            using (retry) retry!.Commit();
            Assert.That(successfulPruning, Is.True);
        }

        [Test]
        public void initial_values_correct()
        {
            TestContext test = new();
            Assert.That(test.FullPruningDb.Name, Is.EqualTo(test.Name));
        }

        [Test]
        public void can_start_pruning()
        {
            TestContext test = new();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.True);
                Assert.That(test.DbIndex, Is.EqualTo(1));
            }
        }

        [Test]
        public void can_not_start_pruning_when_first_in_progress()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out _);
            Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.False);
        }

        [Test]
        public void can_start_second_pruning_when_first_finished()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext context);
            context.Commit();
            context.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.True);
                Assert.That(test.DbIndex, Is.EqualTo(2));
            }
        }

        [Test]
        public void can_start_second_pruning_when_first_cancelled()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext context);
            context.Dispose();
            Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.True);
        }

        [Test]
        public void during_pruning_writes_to_both_dbs()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext _);
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;
            Assert.That(test.CurrentMirrorDb[key], Is.EqualTo(value));
        }

        [Test]
        public void during_pruning_duplicate_on_read([Values] bool spanAction)
        {
            TestContext test = new();
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            byte[] emptyValueKey = { 3, 4 };
            test.FullPruningDb[key] = value;
            test.FullPruningDb[emptyValueKey] = [];

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(KeyValueStoreReads.Read(test.FullPruningDb, key, ReadFlags.None, spanAction), Is.EqualTo(value));
                Assert.That(test.CurrentMirrorDb[key], Is.EqualTo(value));
                Assert.That(KeyValueStoreReads.Read(test.FullPruningDb, emptyValueKey, ReadFlags.None, spanAction), Is.Empty, "an empty value is not a miss");
                Assert.That(KeyValueStoreReads.Read(test.FullPruningDb, [7, 8], ReadFlags.None, spanAction), Is.Null);
            }
        }

        [Test]
        public void during_pruning_do_not_duplicate_read_with_skip_duplicate_read([Values] bool spanAction)
        {
            TestContext test = new();
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(KeyValueStoreReads.Read(test.FullPruningDb, key, ReadFlags.SkipDuplicateRead, spanAction), Is.EqualTo(value));
                Assert.That(test.CurrentMirrorDb[key], Is.Null);
            }
        }

        [Test]
        public void increments_metrics_on_write_to_mirrored_db()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext context);
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;
            context[value] = key;
            Assert.That(test.Metrics, Is.EqualTo(2));
        }

        private class TestContext
        {
            public MemDb CurrentMirrorDb { get; private set; }
            public int DbIndex { get; private set; } = -1;
            public string Name { get; }
            public long Metrics { get; private set; }
            public IDbFactory DbFactory { get; } = Substitute.For<IDbFactory>();
            public FullPruningDb FullPruningDb { get; }

            public TestContext()
            {
                DbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(_ => CurrentMirrorDb = new MemDb((++DbIndex).ToString()));
                Name = "name";
                FullPruningDb = new FullPruningDb(new DbSettings(Name, "path"), DbFactory, () => Metrics++);
            }
        }
    }
}
