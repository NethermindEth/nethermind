// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the three storage paths through <see cref="ReadOnlySlotChanges"/>:
/// empty (count == 0), inline-1 (count == 1, the heap-allocation-skipping fast path),
/// and multi (count &gt; 1, the binary-search path). All branches share the same lookup
/// contract, so each read method is parameterised across all three.
/// </summary>
[TestFixture]
public class ReadOnlySlotChangesTests
{
    private static readonly UInt256 Key = (UInt256)0xbad;

    private static readonly StorageChange[] Empty = [];
    private static readonly StorageChange[] Single = [new StorageChange(5, 42)];
    private static readonly StorageChange[] Multi =
    [
        new StorageChange(0, 100),
        new StorageChange(2, 200),
        new StorageChange(5, 500),
    ];

    private static IEnumerable<TestCaseData> TryGetLastBeforeCases
    {
        get
        {
            yield return new TestCaseData(Empty, 0u, false, 0u).SetName("empty / 0");
            yield return new TestCaseData(Empty, uint.MaxValue, false, 0u).SetName("empty / MaxValue");

            yield return new TestCaseData(Single, 0u, false, 0u).SetName("single@5 / 0");
            yield return new TestCaseData(Single, 5u, false, 0u).SetName("single@5 / 5 (strictly-before excludes self)");
            yield return new TestCaseData(Single, 6u, true, 5u).SetName("single@5 / 6");
            yield return new TestCaseData(Single, uint.MaxValue, true, 5u).SetName("single@5 / MaxValue");

            yield return new TestCaseData(Multi, 0u, false, 0u).SetName("multi / 0");
            yield return new TestCaseData(Multi, 1u, true, 0u).SetName("multi / 1");
            yield return new TestCaseData(Multi, 3u, true, 2u).SetName("multi / 3");
            yield return new TestCaseData(Multi, 5u, true, 2u).SetName("multi / 5 (strictly-before excludes 5 itself)");
            yield return new TestCaseData(Multi, uint.MaxValue, true, 5u).SetName("multi / MaxValue");
        }
    }

    [TestCaseSource(nameof(TryGetLastBeforeCases))]
    public void TryGetLastBefore_returns_last_change_strictly_before_index(
        StorageChange[] changes, uint query, bool expectedFound, uint expectedIndex)
    {
        ReadOnlySlotChanges slot = new(Key, changes);

        bool found = slot.TryGetLastBefore(query, out StorageChange result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(expectedFound));
            if (expectedFound) Assert.That(result.Index, Is.EqualTo(expectedIndex));
            else Assert.That(result, Is.EqualTo(default(StorageChange)));
        }
    }

    private static IEnumerable<TestCaseData> ExactIndexCases
    {
        get
        {
            yield return new TestCaseData(Empty, 0u, false).SetName("empty / 0");

            yield return new TestCaseData(Single, 4u, false).SetName("single@5 / 4");
            yield return new TestCaseData(Single, 5u, true).SetName("single@5 / 5");
            yield return new TestCaseData(Single, 6u, false).SetName("single@5 / 6");

            yield return new TestCaseData(Multi, 0u, true).SetName("multi / 0");
            yield return new TestCaseData(Multi, 1u, false).SetName("multi / 1");
            yield return new TestCaseData(Multi, 2u, true).SetName("multi / 2");
            yield return new TestCaseData(Multi, 5u, true).SetName("multi / 5");
            yield return new TestCaseData(Multi, 6u, false).SetName("multi / 6");
        }
    }

    [TestCaseSource(nameof(ExactIndexCases))]
    public void TryGetAtIndex_and_HasAtIndex_agree_on_exact_index(
        StorageChange[] changes, uint query, bool expected)
    {
        ReadOnlySlotChanges slot = new(Key, changes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.HasAtIndex(query), Is.EqualTo(expected));
            Assert.That(slot.TryGetAtIndex(query, out StorageChange result), Is.EqualTo(expected));
            if (expected) Assert.That(result.Index, Is.EqualTo(query));
            else Assert.That(result, Is.EqualTo(default(StorageChange)));
        }
    }

    private static IEnumerable<TestCaseData> ChangesShapeCases
    {
        get
        {
            yield return new TestCaseData(Empty, 0).SetName("empty");
            yield return new TestCaseData(Single, 1).SetName("single (inline-1)");
            yield return new TestCaseData(Multi, 3).SetName("multi");
        }
    }

    [TestCaseSource(nameof(ChangesShapeCases))]
    public void Changes_length_matches_input(StorageChange[] changes, int expectedLength)
    {
        ReadOnlySlotChanges slot = new(Key, changes);

        Assert.That(slot.Changes, Has.Length.EqualTo(expectedLength));
    }

    [Test]
    public void Inline_single_change_reuses_caller_array_for_Changes()
    {
        // Caller's StorageChange[1] doubles as the singleton cache — Changes must return it
        // without allocating a new array, which is the whole point of the inline-1 path.
        StorageChange[] input = [new StorageChange(5, 42)];
        ReadOnlySlotChanges slot = new(Key, input);

        Assert.That(slot.Changes, Is.SameAs(input));
    }

    private static IEnumerable<TestCaseData> EqualsCases
    {
        get
        {
            yield return new TestCaseData(Empty, Empty, true).SetName("empty == empty");
            yield return new TestCaseData(Single, Single, true).SetName("single == same single");
            yield return new TestCaseData(Single, new[] { new StorageChange(5, 43) }, false).SetName("single != single-with-different-value");
            yield return new TestCaseData(Single, new[] { new StorageChange(6, 42) }, false).SetName("single != single-with-different-index");
            yield return new TestCaseData(Single, Empty, false).SetName("single != empty");
            yield return new TestCaseData(Single, Multi, false).SetName("single != multi");
            yield return new TestCaseData(Multi, Multi, true).SetName("multi == same multi");
            yield return new TestCaseData(Multi, Empty, false).SetName("multi != empty");
        }
    }

    [TestCaseSource(nameof(EqualsCases))]
    public void Equals_compares_structurally_across_count_branches(
        StorageChange[] left, StorageChange[] right, bool expected)
    {
        ReadOnlySlotChanges a = new(Key, left);
        ReadOnlySlotChanges b = new(Key, right);

        Assert.That(a.Equals(b), Is.EqualTo(expected));
    }

    [Test]
    public void Equals_returns_false_for_null()
    {
        ReadOnlySlotChanges slot = new(Key, Single);

        Assert.That(slot.Equals((ReadOnlySlotChanges?)null), Is.False);
    }

    [Test]
    public void Equals_returns_false_for_different_key()
    {
        ReadOnlySlotChanges slot = new(Key, Single);
        ReadOnlySlotChanges otherKey = new((UInt256)0xfee, Single);

        Assert.That(slot.Equals(otherKey), Is.False);
    }
}
