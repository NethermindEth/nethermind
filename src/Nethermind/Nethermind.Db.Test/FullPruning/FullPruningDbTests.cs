// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            test.FullPruningDb[key] = value;

            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(KeyValueStoreReads.Read(test.FullPruningDb, key, ReadFlags.None, spanAction), Is.EqualTo(value));
                Assert.That(test.CurrentMirrorDb[key], Is.EqualTo(value));
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

        [Test]
        public void get_many_batches_on_current_db_without_duplicating_reads([Values] bool pruning)
        {
            TestContext test = new();
            byte[] keys = [1, 2, 3, 4, 5, 6];
            test.FullPruningDb[keys.AsSpan(0, 2)] = [7];
            test.FullPruningDb[keys.AsSpan(4, 2)] = [9];
            if (pruning) test.FullPruningDb.TryStartPruning(duplicateReads: false, out _);

            byte[]?[] values = new byte[]?[3];
            test.FullPruningDb.GetMany(keys, 2, values.AsSpan(), default(CopyDecoder), ReadFlags.HintCacheMiss);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(values, Is.EqualTo(new byte[]?[] { [7], null, [9] }));
                Assert.That(test.MainDb.BatchReads, Is.EqualTo(1));
                Assert.That(test.MainDb.LastBatchFlags, Is.EqualTo(ReadFlags.HintCacheMiss));
                Assert.That(test.CurrentMirrorDb.Get(keys.AsSpan(0, 2)), pruning ? Is.Null : Is.EqualTo(new byte[] { 7 }));
            }
        }

        [Test]
        public void during_pruning_get_many_duplicates_found_values_unless_skipped([Values] bool skipDuplicateRead)
        {
            TestContext test = new();
            byte[] keys = [1, 2, 3, 4, 5, 6];
            test.FullPruningDb[keys.AsSpan(0, 2)] = [7];
            test.FullPruningDb[keys.AsSpan(4, 2)] = [9];
            test.FullPruningDb.TryStartPruning(out IPruningContext _);

            byte[]?[] values = new byte[]?[3];
            test.FullPruningDb.GetMany(keys, 2, values.AsSpan(), default(CopyDecoder),
                skipDuplicateRead ? ReadFlags.SkipDuplicateRead : ReadFlags.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(values, Is.EqualTo(new byte[]?[] { [7], null, [9] }));
                Assert.That(test.MainDb.BatchReads, Is.EqualTo(skipDuplicateRead ? 1 : 0));
                Assert.That(test.CurrentMirrorDb.Get(keys.AsSpan(0, 2)), skipDuplicateRead ? Is.Null : Is.EqualTo(new byte[] { 7 }));
                Assert.That(test.CurrentMirrorDb.Get(keys.AsSpan(2, 2)), Is.Null);
                Assert.That(test.CurrentMirrorDb.Get(keys.AsSpan(4, 2)), skipDuplicateRead ? Is.Null : Is.EqualTo(new byte[] { 9 }));
            }
        }

        private class TestContext
        {
            public MemDb CurrentMirrorDb { get; private set; }
            public BatchingMemDb MainDb { get; }
            public int DbIndex { get; private set; } = -1;
            public string Name { get; }
            public long Metrics { get; private set; }
            public IDbFactory DbFactory { get; } = Substitute.For<IDbFactory>();
            public FullPruningDb FullPruningDb { get; }

            public TestContext()
            {
                DbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(_ => CurrentMirrorDb = new BatchingMemDb((++DbIndex).ToString()));
                Name = "name";
                FullPruningDb = new FullPruningDb(new DbSettings(Name, "path"), DbFactory, () => Metrics++);
                MainDb = (BatchingMemDb)CurrentMirrorDb;
            }
        }

        private sealed class BatchingMemDb(string name) : MemDb(name), IBatchReadOnlyDb
        {
            public int BatchReads { get; private set; }
            public ReadFlags LastBatchFlags { get; private set; }

            void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder, ReadFlags flags)
            {
                BatchReads++;
                LastBatchFlags = flags;
                for (int i = 0; i < values.Length; i++)
                {
                    byte[]? value = Get(keys.Slice(i * keyLength, keyLength), flags);
                    values[i] = value is null ? default! : decoder.Decode(value);
                }
            }
        }

        private readonly struct CopyDecoder : IValueDecoder<byte[]?>
        {
            public byte[]? Decode(ReadOnlySpan<byte> buffer) => buffer.ToArray();
        }
    }
}
