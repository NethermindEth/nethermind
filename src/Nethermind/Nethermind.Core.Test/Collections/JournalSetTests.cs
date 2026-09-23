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

        [Test]
        public void Single_returns_the_only_item()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.Add(3);

            Assert.That(journalSet.First, Is.EqualTo(3));
        }

        /// <remarks>
        /// Items added after a restore reuse the slots the restore freed, which is the one case where set
        /// order stops matching insertion order — so the case discriminates between the two orders.
        /// </remarks>
        [Test]
        public void AsSpan_keeps_insertion_order_when_restored_slots_are_reused()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.AddRange([1, 2, 3]);
            int snapshot = journalSet.TakeSnapshot();
            journalSet.AddRange([4, 5, 6]);
            journalSet.Restore(snapshot);
            journalSet.AddRange([7, 8, 9]);

            int[] insertionOrder = [1, 2, 3, 7, 8, 9];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(journalSet.AsSpan().ToArray(), Is.EqualTo(insertionOrder), "span must follow insertion order, with restored items dropped");
                Assert.That(journalSet, Is.Not.EqualTo(insertionOrder), "set order must differ here, or this case does not discriminate the two orders");
            }
        }
    }
}
