// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections
{
    [Parallelizable(ParallelScope.All)]
    public class ArrayPoolListTests
    {
        [Test]
        public void Empty_list()
        {
            ArrayPoolList<int> list = new(1024);
            list.Should().BeEquivalentTo(Array.Empty<int>());
            list.Count.Should().Be(0);
            list.Capacity.Should().Be(1024);
            list.IsReadOnly.Should().BeFalse();
        }

        [Test]
        public void Add_should_work()
        {
            ArrayPoolList<int> list = new(1024);
            list.AddRange(Enumerable.Range(0, 4));
            list.Should().BeEquivalentTo(Enumerable.Range(0, 4));
        }

        [Test]
        public void Add_should_expand()
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 50));
            list.Should().BeEquivalentTo(Enumerable.Range(0, 50));
            list.Count.Should().Be(50);
            list.Capacity.Should().Be(64);
        }

        [Test]
        public void Clear_should_clear()
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 50));
            list.Clear();
            list.Should().BeEquivalentTo(Array.Empty<int>());
            list.Count.Should().Be(0);
            list.Capacity.Should().Be(64);
        }

        [TestCase(0, ExpectedResult = true)]
        [TestCase(20, ExpectedResult = true)]
        [TestCase(100, ExpectedResult = false)]
        [TestCase(-1, ExpectedResult = false)]
        public bool Contains_should_check_ok(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 50));
            return list.Contains(item);
        }

        [TestCase(0, new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
        [TestCase(4, new[] { 0, 1, 2, 3, -1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
        [TestCase(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, -1 })]
        public void Insert_should_expand(int index, int[] expected)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 16));
            list.Insert(index, -1);
            list.Should().BeEquivalentTo(expected);
        }

        [TestCase(10)]
        [TestCase(-1)]
        public void Insert_should_throw(int index)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            Action action = () => list.Insert(index, -1);
            action.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestCase(0, ExpectedResult = 0)]
        [TestCase(40, ExpectedResult = 40)]
        [TestCase(50, ExpectedResult = -1)]
        [TestCase(-1, ExpectedResult = -1)]
        public int IndexOf_should_return_index(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 50));
            return list.IndexOf(item);
        }


        [TestCase(0, true, new[] { 1, 2, 3, 4, 5, 6, 7 })]
        [TestCase(7, true, new[] { 0, 1, 2, 3, 4, 5, 6 })]
        [TestCase(8, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
        [TestCase(-1, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
        public void Remove_should_remove(int item, bool removed, int[] expected)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            list.Remove(item).Should().Be(removed);
            list.Should().BeEquivalentTo(expected);
        }

        [TestCase(0, new[] { 1, 2, 3, 4, 5, 6, 7 })]
        [TestCase(7, new[] { 0, 1, 2, 3, 4, 5, 6 })]
        public void RemoveAt_should_remove(int item, int[] expected)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            list.RemoveAt(item);
            list.Should().BeEquivalentTo(expected);
        }

        [TestCase(8, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
        [TestCase(-1, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
        public void RemoveAt_should_throw(int item, int[] expected)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            Action action = () => list.RemoveAt(item);
            action.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void CopyTo_should_copy()
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 50));
            int[] array = new int[51];
            list.CopyTo(array, 1);
            array.Should().BeEquivalentTo(Enumerable.Range(0, 1).Concat(Enumerable.Range(0, 50)));
        }

        [TestCase(0, ExpectedResult = 0)]
        [TestCase(7, ExpectedResult = 7)]
        public int Get_should_return(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            return list[item];
        }

        [TestCase(8)]
        [TestCase(-1)]
        public void Get_should_throw(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            Func<int> action = () => list[item];
            action.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestCase(0, ExpectedResult = -1)]
        [TestCase(7, ExpectedResult = -1)]
        public int Set_should_set(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            list[item] = -1;
            return list[item];
        }

        [TestCase(8)]
        [TestCase(-1)]
        public void Set_should_throw(int item)
        {
            ArrayPoolList<int> list = new(4);
            list.AddRange(Enumerable.Range(0, 8));
            Action action = () => list[item] = 1;
            action.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestCase(1, 16)]
        [TestCase(14, 16)]
        [TestCase(15, 32)]
        [TestCase(20, 32)]
        [TestCase(100, 128)]
        public void AddRange_should_expand(int items, int expectedCapacity)
        {
            ArrayPoolList<int> list = new(16) { 0, 1 };
            list.AddRange(Enumerable.Range(2, items));
            list.Should().BeEquivalentTo(Enumerable.Range(0, items + 2));
            list.Capacity.Should().Be(expectedCapacity);
        }
    }
}
