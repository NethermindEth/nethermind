// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

[Parallelizable(ParallelScope.All)]
public class ArrayPoolListTests
{
    [Test]
    public void Empty_list()
    {
        using ArrayPoolList<int> list = new(1024);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
            Assert.That(list.Count, Is.EqualTo(0));
            Assert.That(list.Capacity, Is.EqualTo(1024));
        }
    }

    [Test]
    public void Should_not_hang_when_capacity_is_zero()
    {
        using ArrayPoolList<int> list = new(0);
        Assert.That(list, Is.EqualTo(Array.Empty<int>()));
        list.Add(1);
        Assert.That(list.Count, Is.EqualTo(1));
        list.Remove(1);
        Assert.That(list.Count, Is.EqualTo(0));
        list.Add(1);
        Assert.That(list.Count, Is.EqualTo(1));
    }

    [Test]
    public void Add_should_work()
    {
        using ArrayPoolList<int> list = new(1024);
        list.AddRange(Enumerable.Range(0, 4));
        Assert.That(list, Is.EqualTo(Enumerable.Range(0, 4)));
    }

    [Test]
    public void Add_should_expand()
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EqualTo(Enumerable.Range(0, 50)));
            Assert.That(list.Count, Is.EqualTo(50));
            Assert.That(list.Capacity, Is.EqualTo(64));
        }
    }

    [Test]
    public void Clear_should_clear()
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        list.Clear();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EqualTo(Array.Empty<int>()));
            Assert.That(list.Count, Is.EqualTo(0));
            Assert.That(list.Capacity, Is.EqualTo(64));
        }
    }

    [TestCase(0, ExpectedResult = true)]
    [TestCase(20, ExpectedResult = true)]
    [TestCase(100, ExpectedResult = false)]
    [TestCase(-1, ExpectedResult = false)]
    public bool Contains_should_check_ok(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        return list.Contains(item);
    }

    [Test]
    public void Can_enumerate()
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        Assert.That(list.ToArray(), Is.EqualTo(Enumerable.Range(0, 50)));
    }

    [TestCase(0, new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
    [TestCase(4, new[] { 0, 1, 2, 3, -1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })]
    [TestCase(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, -1 })]
    public void Insert_should_expand(int index, int[] expected)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 16));
        list.Insert(index, -1);
        Assert.That(list, Is.EqualTo(expected));
    }

    [TestCase(10)]
    [TestCase(-1)]
    public void Insert_should_throw(int index)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        Action action = () => list.Insert(index, -1);
        Assert.That(action, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0, ExpectedResult = 0)]
    [TestCase(40, ExpectedResult = 40)]
    [TestCase(50, ExpectedResult = -1)]
    [TestCase(-1, ExpectedResult = -1)]
    public int IndexOf_should_return_index(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        return list.IndexOf(item);
    }


    [TestCase(0, true, new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(7, true, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [TestCase(8, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(-1, false, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    public void Remove_should_remove(int item, bool removed, int[] expected)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        Assert.That(list.Remove(item), Is.EqualTo(removed));
        Assert.That(list, Is.EqualTo(expected));
    }

    [TestCase(0, new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(7, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    public void RemoveAt_should_remove(int item, int[] expected)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        list.RemoveAt(item);
        Assert.That(list, Is.EqualTo(expected));
    }

    [TestCase(8, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [TestCase(-1, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    public void RemoveAt_should_throw(int item, int[] expected)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        Action action = () => list.RemoveAt(item);
        Assert.That(action, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void CopyTo_should_copy()
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 50));
        int[] array = new int[51];
        list.CopyTo(array, 1);
        Assert.That(array, Is.EqualTo(Enumerable.Range(0, 1).Concat(Enumerable.Range(0, 50))));
    }

    [TestCase(0, ExpectedResult = 0)]
    [TestCase(7, ExpectedResult = 7)]
    public int Get_should_return(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        return list[item];
    }

    [TestCase(8)]
    [TestCase(-1)]
    public void Get_should_throw(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        Func<int> action = () => list[item];
        Assert.That(action, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0, ExpectedResult = -1)]
    [TestCase(7, ExpectedResult = -1)]
    public int Set_should_set(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        list[item] = -1;
        return list[item];
    }

    [TestCase(8)]
    [TestCase(-1)]
    public void Set_should_throw(int item)
    {
        using ArrayPoolList<int> list = new(4);
        list.AddRange(Enumerable.Range(0, 8));
        Action action = () => list[item] = 1;
        Assert.That(action, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(1, 16)]
    [TestCase(14, 16)]
    [TestCase(15, 32)]
    [TestCase(20, 32)]
    [TestCase(100, 128)]
    public void AddRange_should_expand(int items, int expectedCapacity)
    {
        using ArrayPoolList<int> list = new(16) { 0, 1 };
        list.AddRange(Enumerable.Range(2, items));
        Assert.That(list, Is.EqualTo(Enumerable.Range(0, items + 2)));
        Assert.That(list.Capacity, Is.EqualTo(expectedCapacity));
    }

    [Test]
    public void Construct_from_ICollection_copies_all_items()
    {
        // HashSet is an ICollection<T> but neither an array nor a list, so it exercises the bulk CopyTo path.
        HashSet<int> source = Enumerable.Range(0, 50).ToHashSet();
        using ArrayPoolList<int> list = new(source.Count, source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EquivalentTo(source));
            Assert.That(list.Count, Is.EqualTo(50));
        }
    }

    [Test]
    public void Should_implement_IList_the_same_as_IListT()
    {
        using ArrayPoolList<int> listT = new(1024);
        IList list = (IList)listT;

        list.Add(1);
        Assert.That(list[0], Is.EqualTo(1));
        Assert.That(list[0], Is.EqualTo(listT[0]));

        list.Insert(1, 2);
        Assert.That(list[1], Is.EqualTo(2));
        Assert.That(list[1], Is.EqualTo(listT[1]));

        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list.Count, Is.EqualTo(listT.Count));

        int[] a = new int[3];

        list.CopyTo(a, 1);
        Assert.That(a[2], Is.EqualTo(2));

        Assert.That(list.Contains(2), Is.EqualTo(listT.Contains(2)));
        Assert.That(list.IndexOf(2), Is.EqualTo(listT.IndexOf(2)));

        list.Remove(2);
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.Count, Is.EqualTo(listT.Count));

        list.Clear();
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.Count, Is.EqualTo(listT.Count));
    }

    [Test]
    public void Should_throw_on_null_insertion_if_null_illegal()
    {
        using ArrayPoolList<int> arrayPoolList = new(1024);
        IList list = (IList)arrayPoolList;

        Action action = () => list.Add(null);
        Assert.That(action, Throws.TypeOf<ArgumentNullException>());

        action = () => list.Insert(0, null);
        Assert.That(action, Throws.TypeOf<ArgumentNullException>());

        action = () => list[0] = null;
        Assert.That(action, Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void Should_throw_on_invalid_type_insertion()
    {
        using ArrayPoolList<int> arrayPoolList = new(1024);
        IList list = (IList)arrayPoolList;

        Action action = () => list.Add(string.Empty);
        Assert.That(action, Throws.TypeOf<InvalidCastException>());

        action = () => list.Insert(0, string.Empty);
        Assert.That(action, Throws.TypeOf<InvalidCastException>());

        action = () => list[0] = string.Empty;
        Assert.That(action, Throws.TypeOf<InvalidCastException>());
    }

    [TestCase("null")]
    [TestCase(null)]
    public void Should_not_throw_on_invalid_type_lookup(object? value)
    {
        using ArrayPoolList<int> arrayPoolList = new(1024);
        IList list = (IList)arrayPoolList;
        list.Add(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Contains(value), Is.False);
            Assert.That(list.IndexOf(value), Is.EqualTo(-1));

            Action action = () => list.Remove(value);
            Assert.That(action, Throws.Nothing);
        }
    }

    [Test]
    public void Should_implement_basic_properties_as_expected()
    {
        using ArrayPoolList<int> list = new(1024);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((ICollection<int>)list).IsReadOnly, Is.False);
            Assert.That(((IList)list).IsReadOnly, Is.False);
            Assert.That(((IList)list).IsFixedSize, Is.False);
            Assert.That(((IList)list).IsSynchronized, Is.False);
            Assert.That(((IList)list).SyncRoot, Is.EqualTo(list));
        }
    }

    [Test]
    public void Dispose_ShouldNotHaveAnEffect_OnEmptyPool()
    {
        ArrayPoolList<int> list = new(0);
        list.Dispose();

        Action act = () => _ = list.Count;
        Assert.That(act, Throws.Nothing);
    }

    [Test]
    public void Can_resize_totally_empty_list()
    {
        using ArrayPoolList<int> list = new(0);
        list.Add(1);
        Assert.That(list.Count, Is.EqualTo(1));
    }

    [Test]
    public void Dispose_recursive()
    {
        ArrayPoolList<ArrayPoolList<int>> list = new(8)
        {
            new ArrayPoolList<int>(8),
            new ArrayPoolList<int>(8),
            new ArrayPoolList<int>(8)
        };

        list.DisposeRecursive();
        list.DisposeRecursive();
    }

    [Test]
    public void RemoveAt_should_not_throw_when_capacity_equals_count()
    {
        ArrayPool<int> pool = new ExactSizeArrayPool<int>();
        using ArrayPoolList<int> list = new(pool, 8, 8);

        for (int i = 0; i < list.Count; i++)
        {
            list[i] = i;
        }

        Action act = () => list.RemoveAt(2);

        Assert.That(act, Throws.Nothing);
        Assert.That(list, Is.EqualTo(new[] { 0, 1, 3, 4, 5, 6, 7 }));
    }

    private sealed class ExactSizeArrayPool<T> : ArrayPool<T>
    {
        public override T[] Rent(int minimumLength) => new T[minimumLength];

        public override void Return(T[] array, bool clearArray = false)
        {
        }
    }

    [Test]
    public void Uninitialized_exposes_requested_count_and_capacity()
    {
        using ArrayPoolList<int> list = new(SafeArrayPool<int>.Shared, 10, 10, clearFirst: false);

        Assert.That(list.Count, Is.EqualTo(10));
        Assert.That(list.Capacity, Is.GreaterThanOrEqualTo(10));
        Assert.That(list.AsSpan().Length, Is.EqualTo(10));
        Assert.That(list.AsMemory().Length, Is.EqualTo(10));
    }

    [Test]
    public void Uninitialized_is_fully_writable_through_span()
    {
        using ArrayPoolList<int> list = new(SafeArrayPool<int>.Shared, 8, 8, clearFirst: false);

        Span<int> span = list.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = i * 7;
        }

        Assert.That(list, Is.EqualTo(Enumerable.Range(0, 8).Select(i => i * 7)));
    }

    [Test]
    public void Uninitialized_with_zero_count_is_empty()
    {
        using ArrayPoolList<int> list = new(SafeArrayPool<int>.Shared, 0, 0, clearFirst: false);

        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.AsSpan().Length, Is.EqualTo(0));
    }

#if DEBUG
    [Test]
    [Explicit("Crashes the test runner")]
    public void Finalizer_throws_if_not_disposed()
    {
        static void CreateAndDrop() => _ = new ArrayPoolList<int>(1);

        bool exception = false;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            exception = true;
        };
        CreateAndDrop();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.That(exception, Is.True);
    }
#endif
}
