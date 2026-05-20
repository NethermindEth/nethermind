// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the three storage paths through <see cref="ReadOnlySlotChanges"/>:
/// empty (count == 0), inline-1 (count == 1, the heap-allocation-skipping fast path),
/// and multi (count &gt; 1, the binary-search path). Each read method
/// (<see cref="ReadOnlySlotChanges.TryGetLastBefore"/>, <c>TryGetAtIndex</c>, <c>HasAtIndex</c>,
/// <c>Changes</c>, <c>Equals</c>) is exercised across all three branches so the
/// inline-1 optimisation stays observationally equivalent to the array path.
/// </summary>
[TestFixture]
public class ReadOnlySlotChangesTests
{
    private static readonly UInt256 Key = (UInt256)0xbad;

    [Test]
    public void Empty_slot_returns_empty_array_for_Changes_and_zero_count()
    {
        ReadOnlySlotChanges slot = new(Key, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.Key, Is.EqualTo(Key));
            Assert.That(slot.Changes, Is.Empty);
        }
    }

    [Test]
    public void Empty_slot_read_methods_return_false()
    {
        ReadOnlySlotChanges slot = new(Key);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TryGetLastBefore(0, out _), Is.False);
            Assert.That(slot.TryGetLastBefore(uint.MaxValue, out _), Is.False);
            Assert.That(slot.TryGetAtIndex(0, out _), Is.False);
            Assert.That(slot.HasAtIndex(0), Is.False);
        }
    }

    [Test]
    public void Single_change_exposes_input_array_unchanged_via_Changes()
    {
        // Caller's StorageChange[1] doubles as the singleton cache — Changes must return it
        // without allocating a new array.
        StorageChange[] input = [new StorageChange(5, 42)];
        ReadOnlySlotChanges slot = new(Key, input);

        Assert.That(slot.Changes, Is.SameAs(input));
    }

    [Test]
    public void Single_change_TryGetLastBefore_returns_only_when_strictly_before()
    {
        ReadOnlySlotChanges slot = new(Key, [new StorageChange(5, 42)]);

        using (Assert.EnterMultipleScope())
        {
            // Below the single change → not strictly before any prior change.
            Assert.That(slot.TryGetLastBefore(5, out StorageChange atFive), Is.False);
            Assert.That(atFive, Is.EqualTo(default(StorageChange)));

            // Below the change index → not strictly before.
            Assert.That(slot.TryGetLastBefore(0, out _), Is.False);

            // Above the change index → returns the inline change.
            Assert.That(slot.TryGetLastBefore(6, out StorageChange atSix), Is.True);
            Assert.That(atSix.Index, Is.EqualTo(5u));
            Assert.That(atSix.Value, Is.EqualTo(new StorageChange(5, 42).Value));

            Assert.That(slot.TryGetLastBefore(uint.MaxValue, out StorageChange atMax), Is.True);
            Assert.That(atMax.Index, Is.EqualTo(5u));
        }
    }

    [Test]
    public void Single_change_TryGetAtIndex_returns_only_on_exact_match()
    {
        ReadOnlySlotChanges slot = new(Key, [new StorageChange(7, 99)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TryGetAtIndex(6, out _), Is.False);
            Assert.That(slot.TryGetAtIndex(8, out _), Is.False);
            Assert.That(slot.TryGetAtIndex(7, out StorageChange hit), Is.True);
            Assert.That(hit.Index, Is.EqualTo(7u));
        }
    }

    [Test]
    public void Single_change_HasAtIndex_matches_only_inline_index()
    {
        ReadOnlySlotChanges slot = new(Key, [new StorageChange(3, 1)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.HasAtIndex(2), Is.False);
            Assert.That(slot.HasAtIndex(3), Is.True);
            Assert.That(slot.HasAtIndex(4), Is.False);
        }
    }

    [Test]
    public void Multi_change_TryGetLastBefore_matches_binary_search_semantics()
    {
        ReadOnlySlotChanges slot = new(Key,
        [
            new StorageChange(0, 100),
            new StorageChange(2, 200),
            new StorageChange(5, 500),
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TryGetLastBefore(0, out _), Is.False);
            Assert.That(slot.TryGetLastBefore(1, out StorageChange atOne), Is.True);
            Assert.That(atOne.Index, Is.EqualTo(0u));
            Assert.That(slot.TryGetLastBefore(3, out StorageChange atThree), Is.True);
            Assert.That(atThree.Index, Is.EqualTo(2u));
            Assert.That(slot.TryGetLastBefore(uint.MaxValue, out StorageChange atMax), Is.True);
            Assert.That(atMax.Index, Is.EqualTo(5u));
        }
    }

    [Test]
    public void Multi_change_TryGetAtIndex_and_HasAtIndex_use_binary_search()
    {
        ReadOnlySlotChanges slot = new(Key,
        [
            new StorageChange(0, 100),
            new StorageChange(2, 200),
            new StorageChange(5, 500),
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.TryGetAtIndex(2, out StorageChange hit), Is.True);
            Assert.That(hit.Index, Is.EqualTo(2u));
            Assert.That(slot.TryGetAtIndex(3, out _), Is.False);
            Assert.That(slot.HasAtIndex(0), Is.True);
            Assert.That(slot.HasAtIndex(5), Is.True);
            Assert.That(slot.HasAtIndex(1), Is.False);
        }
    }

    [Test]
    public void Equals_distinguishes_count_branches()
    {
        // Pins that the inline-1 optimisation does not break equality across the three branches:
        // empty / inline-1 / multi must each compare structurally to a peer of the same shape
        // and reject peers from a different shape.
        ReadOnlySlotChanges empty1 = new(Key, []);
        ReadOnlySlotChanges empty2 = new(Key);
        ReadOnlySlotChanges single1 = new(Key, [new StorageChange(1, 10)]);
        ReadOnlySlotChanges single2 = new(Key, [new StorageChange(1, 10)]);
        ReadOnlySlotChanges singleDifferent = new(Key, [new StorageChange(1, 11)]);
        ReadOnlySlotChanges multi1 = new(Key, [new StorageChange(1, 10), new StorageChange(2, 20)]);
        ReadOnlySlotChanges multi2 = new(Key, [new StorageChange(1, 10), new StorageChange(2, 20)]);
        ReadOnlySlotChanges differentKey = new((UInt256)0xfee, [new StorageChange(1, 10)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(empty1.Equals(empty2), Is.True);
            Assert.That(single1.Equals(single2), Is.True);
            Assert.That(single1.Equals(singleDifferent), Is.False);
            Assert.That(single1.Equals(differentKey), Is.False);
            Assert.That(single1.Equals(empty1), Is.False);
            Assert.That(single1.Equals(multi1), Is.False);
            Assert.That(multi1.Equals(multi2), Is.True);
            Assert.That(multi1.Equals(empty1), Is.False);
            Assert.That(single1.Equals((ReadOnlySlotChanges?)null), Is.False);
        }
    }
}
