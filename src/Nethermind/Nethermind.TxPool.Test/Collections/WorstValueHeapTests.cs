// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.TxPool.Collections;
using NUnit.Framework;

namespace Nethermind.TxPool.Test.Collections;

[Parallelizable(ParallelScope.All)]
public class WorstValueHeapTests
{
    /// <summary>
    /// Replays one seeded sequence of adds, removals, reprices and evictions against a <see cref="SortedSet{T}"/>,
    /// repricing its members the only way it allows: out of the set, changed, then re-added.
    /// </summary>
    [Test]
    public void Max_matches_the_sorted_set_through_random_changes([Range(1, 50)] int seed)
    {
        Random random = new(seed);
        WorstValueHeap<Item, int> heap = new(ItemComparer.Instance);
        SortedSet<Item> set = [with(ItemComparer.Instance)];
        List<Item> members = [];

        for (int step = 0; step < 2_000; step++)
        {
            switch (random.Next(members.Count == 0 ? 1 : 4))
            {
                case 0:
                    Item added = new(random.Next(64), step);
                    Assert.That(heap.Add(added, step), Is.EqualTo(set.Add(added)), $"add, step {step}");
                    members.Add(added);
                    break;
                case 1:
                    Item removed = TakeRandom(members, random);
                    Assert.That(heap.Remove(removed), Is.EqualTo(set.Remove(removed)), $"remove, step {step}");
                    break;
                case 2:
                    Item repriced = members[random.Next(members.Count)];
                    set.Remove(repriced);
                    repriced.Priority = random.Next(64);
                    set.Add(repriced);
                    heap.Update(repriced);
                    break;
                default:
                    Item worst = set.Max;
                    Assert.That(heap.Remove(worst), Is.EqualTo(set.Remove(worst)), $"evict, step {step}");
                    members.Remove(worst);
                    break;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(heap.Count, Is.EqualTo(set.Count), $"count, step {step}");
                Assert.That(heap.Max.Key, Is.SameAs(set.Max), $"max, step {step}");
                Assert.That(heap.Max.Value, Is.EqualTo(set.Max?.Id ?? 0), $"max key, step {step}");
            }
        }
    }

    [Test]
    public void Rejects_a_value_it_already_holds_and_ignores_removing_or_updating_an_absent_one()
    {
        WorstValueHeap<Item, int> heap = new(ItemComparer.Instance);
        Item held = new(1, 1);
        Item absent = new(2, 2);
        heap.Add(held, 1);
        heap.Update(absent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heap.Add(held, 1), Is.False);
            Assert.That(heap.Remove(absent), Is.False);
            Assert.That(heap.Contains(absent), Is.False);
            Assert.That(heap.Max.Key, Is.SameAs(held));
        }
    }

    private static Item TakeRandom(List<Item> items, Random random)
    {
        int index = random.Next(items.Count);
        Item item = items[index];
        items.RemoveAt(index);
        return item;
    }

    private sealed class Item(int priority, int id)
    {
        public int Priority { get; set; } = priority;
        public int Id { get; } = id;
    }

    /// <summary>Strict total order: priority, then the unique id, as the pool comparer ends in a hash tie-break.</summary>
    private sealed class ItemComparer : IComparer<Item>
    {
        public static readonly ItemComparer Instance = new();

        public int Compare(Item x, Item y)
        {
            int byPriority = x.Priority.CompareTo(y.Priority);
            return byPriority != 0 ? byPriority : x.Id.CompareTo(y.Id);
        }
    }
}
