using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Collections;

internal static class ArrayPoolListCore
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GuardResize<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int capacity,
        int count,
        int itemsToAdd = 1)
    {
        int newCount = count + itemsToAdd;

        if (capacity == 0)
        {
            array = pool.Rent(newCount);
            capacity = array.Length;
        }
        else if (newCount > capacity)
        {
            int newCapacity = capacity * 2;
            if (newCapacity == 0) newCapacity = 1;
            while (newCount > newCapacity)
            {
                newCapacity *= 2;
            }

            T[] newArray = pool.Rent(newCapacity);
            Array.Copy(array, 0, newArray, 0, count);
            T[] oldArray = Interlocked.Exchange(ref array, newArray);
            capacity = newArray.Length;
            ClearToCount(oldArray, count);
            pool.Return(oldArray);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearToCount<T>(T[] array, int count)
    {
        if (count > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array, 0, count);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int capacity,
        ref int count,
        T item)
    {
        GuardResize(pool, ref array, ref capacity, count);
        array[count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddRange<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int capacity,
        ref int count,
        ReadOnlySpan<T> items)
    {
        GuardResize(pool, ref array, ref capacity, count, items.Length);
        items.CopyTo(array.AsSpan(count, items.Length));
        count += items.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>(T[] array, ref int count)
    {
        ClearToCount(array, count);
        count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReduceCount<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int capacity,
        ref int count,
        int newCount)
    {
        int oldCount = count;
        if (newCount == oldCount) return;

        if (newCount > oldCount)
            ThrowOnlyReduce(newCount, oldCount);

        count = newCount;

        if (newCount < capacity / 2)
        {
            T[] newArray = pool.Rent(newCount);
            array.AsSpan(0, newCount).CopyTo(newArray);
            T[] oldArray = Interlocked.Exchange(ref array, newArray);
            capacity = newArray.Length;
            ClearToCount(oldArray, oldCount);
            pool.Return(oldArray);
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array, newCount, oldCount - newCount);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOnlyReduce(int newCount, int oldCount)
        {
            throw new ArgumentException($"Count can only be reduced. {newCount} is larger than {oldCount}", nameof(count));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sort<T>(T[] array, int count, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (count > 1) array.AsSpan(0, count).Sort(comparison);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reverse<T>(T[] array, int count)
    {
        array.AsSpan(0, count).Reverse();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains<T>(T[] array, T item, int count) => IndexOf(array, count, item) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf<T>(T[] array, int count, T item)
    {
        int indexOf = Array.IndexOf(array, item);
        return indexOf < count ? indexOf : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyTo<T>(T[] array, int count, T[] destination, int index)
    {
        array.AsMemory(0, count).CopyTo(destination.AsMemory(index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GuardIndex(int index, int count, bool shouldThrow = true, bool allowEqualToCount = false)
    {
        if ((uint)index > (uint)count || (!allowEqualToCount && index == count))
        {
            if (shouldThrow) ThrowArgumentOutOfRangeException();
            return false;
        }
        return true;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowArgumentOutOfRangeException()
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool RemoveAt<T>(T[] array, ref int count, int index, bool shouldThrow)
    {
        bool isValid = GuardIndex(index, count, shouldThrow);
        if (isValid)
        {
            int start = index + 1;
            if (start < count)
            {
                array.AsMemory(start, count - index).CopyTo(array.AsMemory(index));
            }

            count--;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                array[count] = default!;
            }
        }

        return isValid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Remove<T>(T[] array, ref int count, T item)
    {
        return RemoveAt(array, ref count, IndexOf(array, count, item), false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Insert<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int capacity,
        ref int count,
        int index,
        T item)
    {
        GuardResize(pool, ref array, ref capacity, count);
        GuardIndex(index, count, shouldThrow: true, allowEqualToCount: true);
        array.AsMemory(index, count - index).CopyTo(array.AsMemory(index + 1));
        array[index] = item;
        count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Truncate<T>(int newLength, T[] array, ref int count)
    {
        GuardIndex(newLength, count, shouldThrow: true, allowEqualToCount: true);
        count = newLength;
    }

    // Expose Get/Set and GetRef consistent with the original
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetRef<T>(T[] array, int index, int count)
    {
        GuardIndex(index, count);
        return ref array[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(T[] array, int index, int count)
    {
        GuardIndex(index, count);
        return array[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(T[] array, int index, int count, T value)
    {
        GuardIndex(index, count);
        array[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Dispose<T>(
        ArrayPool<T> pool,
        ref T[] array,
        ref int count,
        ref int capacity,
        ref bool disposed)
    {
        // Nothing to do if already disposed or empty.
        if (disposed || capacity == 0)
            return;

        disposed = true;

        T[]? localArray = array;
        array = null!; // safe for ref struct too, doesn't matter if it stays stack-bound.

        if (localArray is not null)
        {
            ClearToCount(localArray, count);
            pool.Return(localArray);
        }

        count = 0;
        capacity = 0;
    }
}
