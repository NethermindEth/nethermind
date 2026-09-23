// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        /// <remarks>
        /// The expected sequence is JournalSet's own contract and unsorted, so sorted order can never satisfy it.
        /// Telling it apart from set order instead rests on an implementation detail: <see cref="HashSet{T}"/>
        /// hands slots freed by the restore to later adds, so its enumeration stops following insertion order.
        /// </remarks>
        [Test]
        public void AsSpan_keeps_insertion_order_when_restored_slots_are_reused()
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
                Assert.That(journalSet.AsSpan().ToArray(), Is.EqualTo(insertionOrder), "span must follow insertion order, with restored items dropped");
                Assert.That(journalSet, Is.Not.EqualTo(insertionOrder),
                    "HashSet<T> no longer reorders reused slots, so this case cannot tell insertion order from set order; " +
                    "not a JournalSet bug, but the construction needs revisiting for the new runtime");
            }
        }
    }
}
