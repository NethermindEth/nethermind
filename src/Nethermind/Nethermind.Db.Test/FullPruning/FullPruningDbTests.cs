// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
            test.FullPruningDb.Name.Should().BeEquivalentTo(test.Name);
        }

        [Test]
        public void can_start_pruning()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out _).Should().BeTrue();
            test.DbIndex.Should().Be(1);
        }

        [Test]
        public void can_not_start_pruning_when_first_in_progress()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out _);
            test.FullPruningDb.TryStartPruning(out _).Should().BeFalse();
        }

        [Test]
        public void can_start_second_pruning_when_first_finished()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext context);
            context.Commit();
            context.Dispose();
            test.FullPruningDb.TryStartPruning(out _).Should().BeTrue();
            test.DbIndex.Should().Be(2);
        }

        [Test]
        public void can_start_second_pruning_when_first_cancelled()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext context);
            context.Dispose();
            test.FullPruningDb.TryStartPruning(out _).Should().BeTrue();
        }

        [Test]
        public void during_pruning_writes_to_both_dbs()
        {
            TestContext test = new();
            test.FullPruningDb.TryStartPruning(out IPruningContext _);
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;
            test.CurrentMirrorDb[key].Should().BeEquivalentTo(value);
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
            test.CurrentMirrorDb[key].Should().BeEquivalentTo(value);
        }

        [Test]
        public void during_pruning_dont_duplicate_read_with_skip_duplicate_read()
        {
            TestContext test = new();
            byte[] key = { 1, 2 };
            byte[] value = { 5, 6 };
            test.FullPruningDb[key] = value;

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            test.FullPruningDb.Get(key, ReadFlags.SkipDuplicateRead);
            test.CurrentMirrorDb[key].Should().BeNull();
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
            test.Metrics.Should().Be(2);
        }

        private class TestContext
        {
            public MemDb CurrentMirrorDb { get; private set; }
            public int DbIndex { get; private set; } = -1;
            public string Name { get; }
            public long Metrics { get; private set; }
            public IRocksDbFactory RocksDbFactory { get; } = Substitute.For<IRocksDbFactory>();
            public FullPruningDb FullPruningDb { get; }

            public TestContext()
            {
                RocksDbFactory.CreateDb(Arg.Any<RocksDbSettings>()).Returns(_ => CurrentMirrorDb = new MemDb((++DbIndex).ToString()));
                Name = "name";
                FullPruningDb = new FullPruningDb(new RocksDbSettings(Name, "path"), RocksDbFactory, () => Metrics++);
            }
        }
    }
}
