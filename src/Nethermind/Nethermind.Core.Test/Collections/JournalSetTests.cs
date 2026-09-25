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

        [Test]
        public void Single_returns_the_only_item()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.Add(3);

            Assert.That(journalSet.First, Is.EqualTo(3));
        }

        [Test]
        public void Default_clear_preserves_hash_set_reuse_order()
        {
            JournalSet<int> journalSet = CreateJournalSet();
            journalSet.Clear();
            journalSet.AddRange([1, 2]);
            journalSet.Restore(-1);
            journalSet.AddRange([4, 5]);

            Assert.That(EnumerateConcrete(journalSet), Is.EqualTo([5, 4]));
        }

        [Test]
        public void Sparse_clear_preserves_insertion_order_across_reuse_cycles()
        {
            JournalSet<int> journalSet = CreateJournalSet(useSparseClear: true);
            journalSet.AddRange(Enumerable.Range(0, 8192));
            journalSet.Restore(2);

            for (int cycle = 0; cycle < 3; cycle++)
            {
                journalSet.Clear();
                int[] expected = [cycle * 10 + 1, cycle * 10 + 2, cycle * 10 + 3, cycle * 10 + 4];
                journalSet.AddRange(expected);

                switch (cycle)
                {
                    case 0:
                        {
                            int[] copied = Enumerable.Repeat(-1, expected.Length + 1).ToArray();
                            journalSet.CopyTo(copied, 1);
                            Assert.That(copied, Is.EqualTo([-1, .. expected]));
                            break;
                        }
                    case 1:
                        Assert.That(EnumerateConcrete(journalSet), Is.EqualTo(expected));
                        break;
                    default:
                        {
                            IEnumerable<int> enumerable = journalSet;
                            using IEnumerator<int> enumerator = enumerable.GetEnumerator();
                            List<int> enumerated = [];
                            while (enumerator.MoveNext())
                            {
                                enumerated.Add(enumerator.Current);
                            }

                            Assert.That(enumerated, Is.EqualTo(expected));
                            break;
                        }
                }
            }
        }

        private static int[] EnumerateConcrete(JournalSet<int> journalSet)
        {
            using JournalSet<int>.Enumerator enumerator = journalSet.GetEnumerator();
            List<int> items = [];
            while (enumerator.MoveNext())
            {
                items.Add(enumerator.Current);
            }

            return items.ToArray();
        }

        [Test]
        public void Sparse_clear_enumerates_restored_items_without_hash_set_normalization()
        {
            JournalSet<int> journalSet = CreateJournalSet(useSparseClear: true);
            journalSet.AddRange(Enumerable.Range(0, 8192));
            journalSet.Restore(1);
            journalSet.Clear();
            journalSet.AddRange([8, 9, 10]);

            // Consume the sparse-clear state before exercising restored and reused entries.
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
            Assert.That(journalSet.First, Is.EqualTo(0));
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
                Assert.That(journalSet.First, Is.EqualTo(7));
                Assert.That(journalSet, Is.EquivalentTo([7, 8]));
            }

            journalSet.Clear();
            Assert.That(journalSet, Is.Empty);
            Assert.That(journalSet.Add(42), Is.True);
            Assert.That(journalSet.Add(42), Is.False);
            Assert.That(journalSet.First, Is.EqualTo(42));
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
