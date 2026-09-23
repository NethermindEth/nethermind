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
    }
}
