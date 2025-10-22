// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

[Parallelizable(ParallelScope.All)]
public class ArrayPoolListRefTests
{
    [Test]
    public void Empty_list()
    {
        using ArrayPoolListRef<int> list = new(1024);
        list.AsSpan().ToArray().Should().BeEquivalentTo(Array.Empty<int>());
        list.Count.Should().Be(0);
        list.Capacity.Should().Be(1024);
    }

    [Test]
    public void Should_not_hang_when_capacity_is_zero()
    {
        using ArrayPoolListRef<int> list = new(0);
        list.AsSpan().ToArray().Should().BeEquivalentTo(Array.Empty<int>());
        list.Add(1);
        list.Count.Should().Be(1);
        list.Remove(1);
        list.Count.Should().Be(0);
        list.Add(1);
        list.Count.Should().Be(1);
    }

    [Test]
    public void Add_should_work()
    {
        using ArrayPoolListRef<int> list = new(1024);
        list.AddRange(Enumerable.Range(0, 4));
        list.AsSpan().ToArray().Should().BeEquivalentTo(Enumerable.Range(0, 4));
    }

    [Test]
    public void Add_should_expand()
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        list.AsSpan().ToArray().Should().BeEquivalentTo(Enumerable.Range(0, 50));
        list.Count.Should().Be(50);
        list.Capacity.Should().Be(64);
    }

    [Test]
    public void Clear_should_clear()
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        list.Clear();
        list.AsSpan().ToArray().Should().BeEquivalentTo(Array.Empty<int>());
        list.Count.Should().Be(0);
        list.Capacity.Should().Be(64);
    }

    [TestCase(0, ExpectedResult = true)]
    [TestCase(20, ExpectedResult = true)]
    [TestCase(100, ExpectedResult = false)]
    [TestCase(-1, ExpectedResult = false)]
    public bool Contains_should_check_ok(int item)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        return list.Contains(item);
    }

    [Test]
    public void Can_enumerate()
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        list.ToArray().Should().BeEquivalentTo(Enumerable.Range(0, 50));
    }

    [TestCase(0, new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
    [TestCase(4, new[] { 0, 1, 2, 3, -1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
    [TestCase(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, -1 })]
    public void Insert_should_expand(int index, int[] expected)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 16));
        list.Insert(index, -1);
        list.AsSpan().ToArray().Should().BeEquivalentTo(expected);
    }

    [TestCase(10)]
    [TestCase(-1)]
    public void Insert_should_throw(int index)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        bool thrown = false;
        try
        {
            list.Insert(index, -1);
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }

        thrown.Should().BeTrue();
    }

    [TestCase(0, ExpectedResult = 0)]
    [TestCase(40, ExpectedResult = 40)]
    [TestCase(50, ExpectedResult = -1)]
    [TestCase(-1, ExpectedResult = -1)]
    public int IndexOf_should_return_index(int item)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        return list.IndexOf(item);
    }


    [TestCase(0, true, new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(7, true, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [TestCase(8, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(-1, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    public void Remove_should_remove(int item, bool removed, int[] expected)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        list.Remove(item).Should().Be(removed);
        list.AsSpan().ToArray().Should().BeEquivalentTo(expected);
    }

    [TestCase(0, new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(7, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    public void RemoveAt_should_remove(int item, int[] expected)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        list.RemoveAt(item);
        list.AsSpan().ToArray().Should().BeEquivalentTo(expected);
    }

    [TestCase(8, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(-1, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    public void RemoveAt_should_throw(int item, int[] expected)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        bool thrown = false;
        try
        {
            list.RemoveAt(item);
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }

        thrown.Should().BeTrue();
    }

    [Test]
    public void CopyTo_should_copy()
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        int[] array = new int[51];
        list.CopyTo(array, 1);
        array.Should().BeEquivalentTo(Enumerable.Range(0, 1).Concat(Enumerable.Range(0, 50)));
    }

    [TestCase(0, ExpectedResult = 0)]
    [TestCase(7, ExpectedResult = 7)]
    public int Get_should_return(int item)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        return list[item];
    }

    [TestCase(8)]
    [TestCase(-1)]
    public void Get_should_throw(int item)
    {
        using ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        bool thrown = false;
        try
        {
            int _ = list[item];
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }

        thrown.Should().BeTrue();
    }

    [TestCase(0, ExpectedResult = -1)]
    [TestCase(7, ExpectedResult = -1)]
    public int Set_should_set(int item)
    {
        ArrayPoolListRef<int> list = new(4);
        try
        {
            list.AddRange(Enumerable.Range(0, 8));
            list[item] = -1;
            return list[item];
        }
        finally
        {
            list.Dispose();
        }
    }

    [TestCase(8)]
    [TestCase(-1)]
    public void Set_should_throw(int item)
    {
        ArrayPoolListRef<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        bool thrown = false;
        try
        {
            list[item] = 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            thrown = true;
        }
        finally
        {
            list.Dispose();
        }

        thrown.Should().BeTrue();
    }

    [TestCase(1, 16)]
    [TestCase(14, 16)]
    [TestCase(15, 32)]
    [TestCase(20, 32)]
    [TestCase(100, 128)]
    public void AddRange_should_expand(int items, int expectedCapacity)
    {
        using ArrayPoolListRef<int> list = new(16, [0, 1]);
        list.AddRange(Enumerable.Range(2, items));
        list.AsSpan().ToArray().Should().BeEquivalentTo(Enumerable.Range(0, items + 2));
        list.Capacity.Should().Be(expectedCapacity);
    }

    [Test]
    public void Dispose_ShouldNotHaveAnEffect_OnEmptyPool()
    {
        var list = new ArrayPoolListRef<int>(0);
        list.Dispose();
        int _ = list.Count;
    }

    [Test]
    public void Can_resize_totally_empty_list()
    {
        using ArrayPoolListRef<int> list = new(0);
        list.Add(1);
        list.Count.Should().Be(1);
    }

#if DEBUG
    [Test]
    [Explicit("Crashes the test runner")]
    public void Finalizer_throws_if_not_disposed()
    {
        static void CreateAndDrop()
        {
            ArrayPoolListRef<int> list = new(1);
        }

        bool exception = false;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            exception = true;
        };
        CreateAndDrop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        exception.Should().BeTrue();
    }
#endif
}
