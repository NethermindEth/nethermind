// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    public class LinkedHashSetTests
    {
        private static readonly int[] _defaultSet = new[] { 1, 2, 3 };
        private static readonly int _unknownElement = 100;
        private readonly int[] _testSet = new[] { 2, _unknownElement };

        [Test]
        public void new_is_empty()
        {
            LinkedHashSet<int> linkedHashSet = new();
            linkedHashSet.Should().BeEquivalentTo(Enumerable.Empty<int>());
            linkedHashSet.Count.Should().Be(0);
        }

        [Test]
        public void initializes_from_enumerable()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            linkedHashSet.Should().BeEquivalentTo(_defaultSet);
            linkedHashSet.Count.Should().Be(_defaultSet.Length);
        }

        [Test]
        public void initializes_with_capacity()
        {
            LinkedHashSet<int> linkedHashSet = new(2);
            for (int i = 0; i < _defaultSet.Length; i++)
            {
                linkedHashSet.Add(_defaultSet[i]);
            }
            linkedHashSet.Should().BeEquivalentTo(_defaultSet);
            linkedHashSet.Count.Should().Be(_defaultSet.Length);
        }

        [Test]
        public void ignores_adding_duplicates()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            for (int i = 0; i < _defaultSet.Length; i++)
            {
                linkedHashSet.Add(_defaultSet[i]);
            }
            linkedHashSet.Should().BeEquivalentTo(_defaultSet);
            linkedHashSet.Count.Should().Be(_defaultSet.Length);
        }

        [Test]
        public void can_clear()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            linkedHashSet.Clear();
            linkedHashSet.Should().BeEquivalentTo(Enumerable.Empty<int>());
            linkedHashSet.Count.Should().Be(0);
        }

        [Test]
        public void can_copy()
        {
            int[] array = new int[4];
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            linkedHashSet.CopyTo(array, 1);
            array.Skip(1).Should().BeEquivalentTo(_defaultSet);
        }

        [Test]
        public void contains_added_elements()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            _defaultSet.Select(i => linkedHashSet.Contains(i)).Should().AllBeEquivalentTo(true);
        }

        [Test]
        public void not_contains_not_added_elements()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            linkedHashSet.Contains(_unknownElement).Should().BeFalse();
        }

        [Test]
        public void removes_elements([Values(false, true)] bool reverse)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            IEnumerable<int> toDelete = reverse ? _defaultSet.Reverse() : _defaultSet;
            int expectedCount = linkedHashSet.Count;
            foreach (int i in toDelete)
            {
                linkedHashSet.Remove(i).Should().BeTrue();
                linkedHashSet.Count.Should().Be(--expectedCount);
            }

            linkedHashSet.Should().BeEquivalentTo(Enumerable.Empty<int>());
        }

        [Test]
        public void not_removes_unknown_elements()
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            linkedHashSet.Remove(_unknownElement).Should().BeFalse();
            linkedHashSet.Should().BeEquivalentTo(_defaultSet);
            linkedHashSet.Count.Should().Be(_defaultSet.Length);
        }


        [Test]
        public void except_with()
        {
            ChangeSetTest(_defaultSet.Except(_testSet), s => s.ExceptWith(_testSet));
        }

        [Test]
        public void intersect_with()
        {
            ChangeSetTest(_defaultSet.Intersect(_testSet), s => s.IntersectWith(_testSet));
        }

        [Test]
        public void symmetric_except_with()
        {
            ChangeSetTest(_defaultSet.Concat(_testSet).Except(_defaultSet.Intersect(_testSet)), s => s.SymmetricExceptWith(_testSet));
        }

        [Test]
        public void union_with()
        {
            ChangeSetTest(_defaultSet.Union(_testSet), s => s.UnionWith(_testSet));
        }

        private void ChangeSetTest(IEnumerable<int> expected, Action<LinkedHashSet<int>> action)
        {
            expected = expected as int[] ?? expected.ToArray();
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            action(linkedHashSet);
            linkedHashSet.Count.Should().Be(expected.Count());
            linkedHashSet.Should().BeEquivalentTo(expected);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = true)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = false)]
        public bool set_equals(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.SetEquals(set);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = true)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = true)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = true)]
        [TestCase(new[] { 4 }, ExpectedResult = false)]
        public bool overlaps(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.Overlaps(set);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = true)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = false)]
        [TestCase(new[] { 5, 4, 3, 2, 1 }, ExpectedResult = true)]
        public bool is_subset_of(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.IsSubsetOf(set);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = true)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = true)]
        [TestCase(new[] { 5, 4, 3, 2, 1 }, ExpectedResult = false)]
        public bool is_superset_of(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.IsSupersetOf(set);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = false)]
        [TestCase(new[] { 5, 4, 3, 2, 1 }, ExpectedResult = true)]
        public bool is_proper_subset_of(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.IsProperSubsetOf(set);
        }

        [TestCase(new[] { 1, 2, 3 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 3, 4 }, ExpectedResult = false)]
        [TestCase(new[] { 1, 2 }, ExpectedResult = true)]
        [TestCase(new[] { 5, 4, 3, 2, 1 }, ExpectedResult = false)]
        public bool is_proper_superset_of(IEnumerable<int> set)
        {
            LinkedHashSet<int> linkedHashSet = new(_defaultSet);
            return linkedHashSet.IsProperSupersetOf(set);
        }
    }
}
