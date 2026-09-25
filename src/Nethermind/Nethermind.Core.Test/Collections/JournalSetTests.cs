// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class JournalSetTests
    {
        private static JournalSet<int> CreateJournalSet(bool useSparseClear = false) => new(EqualityComparer<int>.Default, useSparseClear);

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
        public void Enumerates_in_insertion_order_when_restored_slots_are_reused()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange([3, 1, 2]);
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange([6, 4, 5]);
            journalSet.Restore(snapshot);
            journalSet.AddRange([9, 7, 8]);

            int[] insertionOrder = [3, 1, 2, 9, 7, 8];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet, Is.EqualTo(insertionOrder), "enumeration");
                Assert.That(journalSet.ToArray(), Is.EqualTo(insertionOrder), "CopyTo");
            }
        }

        private static int[] EnumerateConcrete(JournalSet<int> journalSet)
        {
            using List<int>.Enumerator enumerator = journalSet.GetEnumerator();
            List<int> items = [];
            while (enumerator.MoveNext())
            {
                items.Add(enumerator.Current);
            }

            return items.ToArray();
        }

        [Test]
        public void Sparse_journal_enumerates_in_journal_order_after_restore_and_readd()
        {
            JournalSet<int> journalSet = CreateJournalSet(useSparseClear: true);
            journalSet.AddRange(Enumerable.Range(0, 8192));
            journalSet.Restore(1);
            journalSet.Clear();
            journalSet.AddRange([8, 9, 10]);

            Assert.That(EnumerateConcrete(journalSet), Is.EqualTo([8, 9, 10]));
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange([11, 12]);
            journalSet.Restore(snapshot);
            journalSet.AddRange([13, 14]);

            int[] expected = [8, 9, 10, 13, 14];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(EnumerateConcrete(journalSet), Is.EqualTo(expected));

                int[] copied = Enumerable.Repeat(-1, expected.Length + 1).ToArray();
                journalSet.CopyTo(copied, 1);
                Assert.That(copied, Is.EqualTo([-1, .. expected]));

                IEnumerable<int> generic = journalSet;
                Assert.That(generic, Is.EqualTo(expected));

                IEnumerable nongeneric = journalSet;
                List<int> enumerated = [];
                foreach (object item in nongeneric)
                {
                    enumerated.Add((int)item);
                }

                Assert.That(enumerated, Is.EqualTo(expected));
            }
        }

        [Test]
        public void Sparse_clear_preserves_add_and_restore_semantics_after_large_growth()
        {
            JournalSet<int> journalSet = CreateJournalSet(useSparseClear: true);
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
            JournalSet<string> journalSet = new(EqualityComparer<string>.Default, useSparseClear: true);
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
    }
}
