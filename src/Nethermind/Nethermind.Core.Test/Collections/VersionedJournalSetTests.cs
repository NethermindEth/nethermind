// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class VersionedJournalSetTests
    {
        private static VersionedJournalSet<int> CreateSet() => new(EqualityComparer<int>.Default);

        [Test]
        public void Can_restore_snapshot()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.AddRange(Enumerable.Range(0, 10));
            int snapshot = set.TakeSnapshot();
            set.AddRange(Enumerable.Range(10, 10));
            set.Restore(snapshot);
            Assert.That(set, Is.EqualTo(Enumerable.Range(0, 10)));
        }

        [Test]
        public void Can_restore_empty_snapshot_on_empty()
        {
            VersionedJournalSet<int> set = CreateSet();
            int snapshot = set.TakeSnapshot();
            set.Restore(snapshot);
            set.Restore(snapshot);
            Assert.That(set, Is.EqualTo(Enumerable.Empty<int>()));
        }

        [Test]
        public void Can_restore_empty_snapshot()
        {
            VersionedJournalSet<int> set = CreateSet();
            int snapshot = set.TakeSnapshot();
            set.AddRange(Enumerable.Range(0, 10));
            set.Restore(snapshot);
            set.Restore(snapshot);
            Assert.That(set, Is.EqualTo(Enumerable.Empty<int>()));
        }

        [Test]
        public void Snapshots_behave_as_sets()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.AddRange(Enumerable.Range(0, 10));
            int snapshot = set.TakeSnapshot();
            set.AddRange(Enumerable.Range(0, 20));
            set.Restore(snapshot);
            Assert.That(set, Is.EqualTo(Enumerable.Range(0, 10)));
        }

        [Test]
        public void Add_returns_true_only_on_cold_to_warm_transition()
        {
            VersionedJournalSet<int> set = CreateSet();
            Assert.That(set.Add(1), Is.True);
            Assert.That(set.Add(1), Is.False);
            Assert.That(set.Contains(1), Is.True);
        }

        [Test]
        public void Reset_makes_all_items_cold()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.AddRange(Enumerable.Range(0, 10));
            set.Reset();
            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(set, Is.EqualTo(Enumerable.Empty<int>()));
            Assert.That(set.Contains(5), Is.False);
            // Retained entries re-warm correctly in the new epoch.
            Assert.That(set.Add(5), Is.True);
            Assert.That(set.Contains(5), Is.True);
            Assert.That(set.Count, Is.EqualTo(1));
        }

        [Test]
        public void Restore_works_after_reset_with_retained_entries()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.AddRange(Enumerable.Range(0, 10));
            set.Reset();

            set.AddRange(Enumerable.Range(5, 5));
            int snapshot = set.TakeSnapshot();
            set.AddRange(Enumerable.Range(10, 5));
            set.Restore(snapshot);

            Assert.That(set, Is.EqualTo(Enumerable.Range(5, 5)));
            Assert.That(set.Contains(12), Is.False);
            Assert.That(set.Add(12), Is.True);
        }

        [Test]
        public void Restored_items_can_be_added_again_in_same_epoch()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.Add(1);
            int snapshot = set.TakeSnapshot();
            set.Add(2);
            set.Restore(snapshot);

            Assert.That(set.Contains(2), Is.False);
            Assert.That(set.Add(2), Is.True);
            Assert.That(set, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Clear_drops_everything()
        {
            VersionedJournalSet<int> set = CreateSet();
            set.AddRange(Enumerable.Range(0, 10));
            set.Clear();
            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(set.Contains(1), Is.False);
            Assert.That(set.Add(1), Is.True);
        }
    }
}
