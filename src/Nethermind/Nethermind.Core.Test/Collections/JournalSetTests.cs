// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class JournalSetTests
    {
        private static JournalSet<int> CreateJournalSet() => new(EqualityComparer<int>.Default);

        [Test]
        public void Can_restore_snapshot()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange(Enumerable.Range(0, 10));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(10, 10));
            journalSet.Restore(snapshot);
            Assert.That(journalSet, Is.EqualTo(Enumerable.Range(0, 10)));
        }

        [Test]
        public void Can_restore_empty_snapshot_on_empty()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            int snapshot = journalSet.TakeSnapshot();
            journalSet.Restore(snapshot);
            journalSet.Restore(snapshot);
            Assert.That(journalSet, Is.EqualTo(Enumerable.Empty<int>()));
        }

        [Test]
        public void Can_restore_empty_snapshot()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(0, 10));
            journalSet.Restore(snapshot);
            journalSet.Restore(snapshot);
            Assert.That(journalSet, Is.EqualTo(Enumerable.Empty<int>()));
        }

        [Test]
        public void Snapshots_behave_as_sets()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange(Enumerable.Range(0, 10));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(0, 20));
            journalSet.Restore(snapshot);
            Assert.That(journalSet, Is.EqualTo(Enumerable.Range(0, 10)));
        }

        /// <remarks>Adds after a restore refill the freed slots, where a hash set's own order stops following insertion order.</remarks>
        [Test]
        public void Enumerates_in_insertion_order_when_restored_slots_are_reused([Values] bool afterSparseClear)
        {
            JournalSet<int> journalSet = CreateJournalSet();
            if (afterSparseClear)
            {
                journalSet.AddRange(Enumerable.Range(100, 8192));
                journalSet.Restore(0);
                journalSet.Clear();
            }

            journalSet.AddRange([3, 1, 2]);
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange([6, 4, 5]);
            journalSet.Restore(snapshot);
            journalSet.AddRange([9, 7, 8]);

            int[] insertionOrder = [3, 1, 2, 9, 7, 8];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.EqualTo(insertionOrder), "enumeration");
                Assert.That(journalSet, Is.SequenceEqualTo(insertionOrder), "CopyTo");
            }
        }

        [Test]
        public void Sparse_clear_preserves_add_and_restore_semantics_after_large_growth()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange(Enumerable.Range(0, 8192));
            journalSet.Restore(0);

            Assert.That(journalSet, Is.EquivalentTo([0]));
            journalSet.Clear();
            Assert.That(journalSet, Is.Empty);

            int emptySnapshot = journalSet.TakeSnapshot();
            Assert.That(journalSet.Add(7), Is.True);
            Assert.That(journalSet.Add(7), Is.False);
            Assert.That(journalSet.Add(8), Is.True);
            journalSet.Restore(emptySnapshot);
            Assert.That(journalSet, Is.Empty);

            Assert.That(journalSet.Add(7), Is.True);
            Assert.That(journalSet.Add(8), Is.True);
            int nonemptySnapshot = journalSet.TakeSnapshot();
            Assert.That(journalSet.Add(9), Is.True);
            Assert.That(journalSet.Add(7), Is.False);
            journalSet.Restore(nonemptySnapshot);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet.Count, Is.EqualTo(2));
                Assert.That(journalSet.Contains(7), Is.True);
                Assert.That(journalSet.Contains(8), Is.True);
                Assert.That(journalSet.Contains(9), Is.False);
                Assert.That(journalSet, Is.EquivalentTo([7, 8]));
            }

            journalSet.Clear();
            Assert.That(journalSet, Is.Empty);
            Assert.That(journalSet.Add(42), Is.True);
            Assert.That(journalSet.Add(42), Is.False);
        }

        [Test]
        public void Sparse_clear_supports_reference_items()
        {
            JournalSet<string> journalSet = new(EqualityComparer<string>.Default);
            journalSet.AddRange(Enumerable.Range(0, 4096).Select(static i => i.ToString()));
            journalSet.Restore(0);
            journalSet.Clear();

            string item = new('x', 1);
            Assert.That(journalSet.Add(item), Is.True);
            Assert.That(journalSet.Add(new string('x', 1)), Is.False);
            journalSet.Clear();

            Assert.That(journalSet, Is.Empty);
            Assert.That(journalSet.Contains(item), Is.False);
        }

        /// <remarks>The set grows several times past the snapshot, so the restore removes items that the growth rehashed.</remarks>
        [Test]
        public void Restore_across_growth_drops_only_the_newer_items()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange(Enumerable.Range(0, 5));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(5, 2000));
            journalSet.Restore(snapshot);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.EqualTo(Enumerable.Range(0, 5)));
                Assert.That(Enumerable.Range(0, 5).All(item => journalSet.Contains(item)), Is.True, "the older items stay");
                Assert.That(Enumerable.Range(5, 2000).Any(item => journalSet.Contains(item)), Is.False, "the newer items are gone");
            }

            Assert.That(journalSet.Add(1000), Is.True, "a dropped item adds again");
            Assert.That(journalSet.Add(3), Is.False, "a kept item is still present");
        }

        /// <remarks>A weak hash puts many items in one bucket, so removals by restore and clear run through long collision chains.</remarks>
        [Test]
        public void Matches_a_reference_journal_under_random_adds_restores_and_clears([Values(1, 3, 64, int.MaxValue)] int distinctHashes)
        {
            Random random = new(distinctHashes);
            JournalSet<int> journalSet = new(new ModuloComparer(distinctHashes));
            List<int> expected = [];
            Stack<int> snapshots = new();
            const int keys = 400;

            for (int step = 0; step < 20_000; step++)
            {
                int roll = random.Next(100);
                if (roll < 70)
                {
                    int item = random.Next(keys);
                    Assert.That(journalSet.Add(item), Is.EqualTo(!expected.Contains(item)), $"Add({item}) at step {step}");
                    if (!expected.Contains(item)) expected.Add(item);
                }
                else if (roll < 82)
                {
                    snapshots.Push(journalSet.TakeSnapshot());
                }
                else if (roll < 97)
                {
                    if (snapshots.Count == 0) continue;
                    int snapshot = snapshots.Pop();
                    journalSet.Restore(snapshot);
                    expected.RemoveRange(snapshot + 1, expected.Count - snapshot - 1);
                }
                else
                {
                    journalSet.Clear();
                    expected.Clear();
                    snapshots.Clear();
                }

                Assert.That(journalSet.Count, Is.EqualTo(expected.Count), $"Count at step {step}");
                if (step % 97 == 0)
                {
                    Assert.That(journalSet, Is.EqualTo(expected), $"insertion order at step {step}");
                    for (int key = 0; key < keys; key++)
                    {
                        Assert.That(journalSet.Contains(key), Is.EqualTo(expected.Contains(key)), $"Contains({key}) at step {step}");
                    }
                }
            }
        }

        /// <remarks>The first clear empties a large set wholesale; the second removes its few items one by one.</remarks>
        [Test]
        public void Dense_then_sparse_clear_keeps_add_and_restore_working()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange(Enumerable.Range(0, 10_000));
            journalSet.Clear();

            journalSet.AddRange([10_000, 10_001, 10_002]);
            journalSet.Clear();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.Empty);
                Assert.That(journalSet.Contains(10_001), Is.False);
                Assert.That(Enumerable.Range(0, 10_000).Any(item => journalSet.Contains(item)), Is.False);
            }

            journalSet.AddRange(Enumerable.Range(0, 100));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange(Enumerable.Range(50, 100));
            journalSet.Restore(snapshot);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.EqualTo(Enumerable.Range(0, 100)));
                Assert.That(journalSet.Contains(120), Is.False);
            }
        }

        [Test]
        public void Restore_beyond_the_current_position_throws_and_changes_nothing()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange([1, 2, 3]);

            Assert.That(() => journalSet.Restore(3), Throws.InvalidOperationException);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.EqualTo([1, 2, 3]));
                Assert.That(journalSet.Contains(2), Is.True);
            }
        }

        private sealed class ModuloComparer(int distinctHashes) : EqualityComparer<int>
        {
            public override bool Equals(int x, int y) => x == y;
            public override int GetHashCode(int obj) => obj % distinctHashes;
        }
    }
}
