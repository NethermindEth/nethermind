// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

internal static unsafe class NativeMemoryListCore<T> where T : unmanaged
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GuardResize(
        ref T* ptr,
        ref int capacity,
        int count,
        int itemsToAdd = 1)
    {
        // Compute newCount as long to detect overflow past int.MaxValue. The element
        // count itself is bounded by int.MaxValue (Count returns int); throw OOM when
        // the caller would push past that ceiling instead of silently writing past
        // the buffer.
        long newCountLong = (long)count + itemsToAdd;
        if (newCountLong <= capacity) return;
        if (newCountLong > int.MaxValue)
            throw new OutOfMemoryException($"NativeMemoryList<{typeof(T).Name}> exceeded int.MaxValue elements (requested {newCountLong}).");
        int newCount = (int)newCountLong;

        // Doubling growth, computed via long so the *2 step doesn't overflow int when
        // capacity > int.MaxValue / 2. Clamp at int.MaxValue.
        long newCapacityLong = capacity == 0 ? 1 : (long)capacity * 2;
        while (newCount > newCapacityLong) newCapacityLong *= 2;
        if (newCapacityLong > int.MaxValue) newCapacityLong = int.MaxValue;
        int newCapacity = (int)newCapacityLong;

        T* newPtr = (T*)NativeMemory.Alloc((nuint)newCapacity, (nuint)sizeof(T));
        if (count > 0)
        {
            Buffer.MemoryCopy(ptr, newPtr, (long)newCapacity * sizeof(T), (long)count * sizeof(T));
        }
        if (ptr is not null) NativeMemory.Free(ptr);
        ptr = newPtr;
        capacity = newCapacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(ref T* ptr, ref int capacity, ref int count, T item)
    {
        GuardResize(ref ptr, ref capacity, count);
        ptr[count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddRange(ref T* ptr, ref int capacity, ref int count, ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return;
        GuardResize(ref ptr, ref capacity, count, items.Length);
        items.CopyTo(new Span<T>(ptr + count, items.Length));
        count += items.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(ref int count) => count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReduceCount(ref T* ptr, ref int capacity, ref int count, int newCount)
    {
        if (newCount == count) return;
        if (newCount > count) ThrowOnlyReduce(newCount, count);

        count = newCount;

        if (newCount < capacity / 2 && newCount > 0)
        {
            T* newPtr = (T*)NativeMemory.Alloc((nuint)newCount, (nuint)sizeof(T));
            Buffer.MemoryCopy(ptr, newPtr, (long)newCount * sizeof(T), (long)newCount * sizeof(T));
            NativeMemory.Free(ptr);
            ptr = newPtr;
            capacity = newCount;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOnlyReduce(int newCount, int oldCount) =>
            throw new ArgumentException($"Count can only be reduced. {newCount} is larger than {oldCount}", nameof(count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sort(T* ptr, int count, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (count > 1) new Span<T>(ptr, count).Sort(comparison);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sort<TComparer>(T* ptr, int count, TComparer comparer)
        where TComparer : IComparer<T>
    {
        if (count > 1) new Span<T>(ptr, count).Sort(comparer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reverse(T* ptr, int count) => new Span<T>(ptr, count).Reverse();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(T* ptr, int count, T item) =>
        new ReadOnlySpan<T>(ptr, count).IndexOf(item);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(T* ptr, int count, T item) => IndexOf(ptr, count, item) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyTo(T* ptr, int count, T[] destination, int index) =>
        new ReadOnlySpan<T>(ptr, count).CopyTo(destination.AsSpan(index));

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
        static void ThrowArgumentOutOfRangeException() => throw new ArgumentOutOfRangeException(nameof(index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool RemoveAt(T* ptr, ref int count, int index, bool shouldThrow)
    {
        bool isValid = GuardIndex(index, count, shouldThrow);
        if (isValid)
        {
            int start = index + 1;
            if (start < count)
            {
                new Span<T>(ptr + start, count - start).CopyTo(new Span<T>(ptr + index, count - index));
            }
            count--;
        }
        return isValid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Remove(T* ptr, ref int count, T item) =>
        RemoveAt(ptr, ref count, IndexOf(ptr, count, item), shouldThrow: false);

    public static T? RemoveLast(T* ptr, ref int count)
    {
        if (count > 0)
        {
            int index = count - 1;
            T item = ptr[index];
            count--;
            return item;
        }
        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Insert(ref T* ptr, ref int capacity, ref int count, int index, T item)
    {
        GuardIndex(index, count, shouldThrow: true, allowEqualToCount: true);
        GuardResize(ref ptr, ref capacity, count);
        if (index < count)
        {
            new Span<T>(ptr + index, count - index).CopyTo(new Span<T>(ptr + index + 1, count - index));
        }
        ptr[index] = item;
        count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Truncate(int newLength, ref int count)
    {
        GuardIndex(newLength, count, shouldThrow: true, allowEqualToCount: true);
        count = newLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetRef(T* ptr, int index, int count)
    {
        GuardIndex(index, count);
        return ref ptr[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get(T* ptr, int index, int count)
    {
        GuardIndex(index, count);
        return ptr[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(T* ptr, int index, int count, T value)
    {
        GuardIndex(index, count);
        ptr[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Dispose(ref T* ptr, ref int count, ref int capacity)
    {
        T* local = ptr;
        ptr = null;
        if (local is not null) NativeMemory.Free(local);
        count = 0;
        capacity = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Dispose(ref T* ptr, ref int count, ref int capacity, ref bool disposed)
    {
        if (!disposed)
        {
            disposed = true;
            Dispose(ref ptr, ref count, ref capacity);
        }
    }
}
