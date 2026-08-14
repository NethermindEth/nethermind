// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db.FullPruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.FullPruning
{
    [Parallelizable(ParallelScope.All)]
    public class FullPruningDbTests
    {
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
            Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.True);
            Assert.That(test.DbIndex, Is.EqualTo(1));
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
            Assert.That(test.FullPruningDb.TryStartPruning(out _), Is.True);
            Assert.That(test.DbIndex, Is.EqualTo(2));
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
        public void during_pruning_duplicate_on_read()
        {
            TestContext test = new();
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            test.FullPruningDb.Get(key);
            Assert.That(test.CurrentMirrorDb[key], Is.EqualTo(value));
        }

        [Test]
        public void during_pruning_do_not_duplicate_read_with_skip_duplicate_read()
        {
            TestContext test = new();
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            test.FullPruningDb.Get(key, ReadFlags.SkipDuplicateRead);
            Assert.That(test.CurrentMirrorDb[key], Is.Null);
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
