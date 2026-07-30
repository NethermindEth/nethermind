// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

// Safety invariant: after every successful public operation, count <= capacity holds, where
// capacity is the element count of the buffer currently at ptr (returned by AllocateBuffer as
// `actualCapacity` and tracked through GuardResize / ReduceCount). Writes go through
// GuardResize, which grows the buffer before the write; reads go through GuardIndex, which
// bounds-checks against count. FreeBuffer is always called with the exact (ptr, pooledArray,
// pinHandle) triple returned by the matching AllocateBuffer call — pool-backed and
// native-allocated buffers are never crossed, so the right free path runs in every case.
internal static unsafe class NativeMemoryListCore<T> where T : unmanaged
{
    // Buffers requested below this byte size route through ArrayPool<T>.Shared (pinned)
    // instead of NativeMemory.Alloc, to avoid per-allocation malloc round-trips on hot,
    // short-lived collections. The decision is made on the requested capacity; the pool
    // may overshoot, but we stay on pool until a resize would push us above the threshold.
    internal const int PoolThresholdBytes = 1024;

    // When sizeof(T) is a power of two we route the native-alloc path through
    // NativeMemory.AlignedAlloc so the returned pointer is aligned to the element
    // size. This makes element accesses naturally aligned (SIMD loads, Interlocked
    // ops on multi-word structs, cache-line packing for slot tables, etc.) — the
    // common case for primitives and tightly-packed value types. Non-power-of-two
    // sizes fall back to NativeMemory.Alloc, which only guarantees malloc-default
    // alignment. The branch is on a `sizeof(T)`-derived constant so the JIT folds
    // it away per generic instantiation. ArrayPool-backed allocations are pinned
    // to a managed array and stay on the unaligned path; callers that need element
    // alignment should size the buffer above PoolThresholdBytes.
    //
    // ZK_EVM omits AlignedAlloc entirely because the runtime in those environments
    // can fault on aligned-alloc — same carve-out KeccakCache uses.
    // A property (not a const) even in the ZK_EVM case: a compile-time-constant
    // `false` here would make the AlignedAlloc/AlignedFree branches unreachable
    // and trip CS0162 (warnings-as-errors). The JIT still folds the constant get
    // body and drops the dead branch per generic instantiation.
    private static bool UseAlignedAlloc
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if ZK_EVM
        get => false;
#else
        get => BitOperations.IsPow2(sizeof(T));
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T* AllocateBuffer(int capacity, out T[]? pooledArray, out GCHandle pinHandle, out int actualCapacity)
    {
        if (capacity == 0)
        {
            pooledArray = null;
            pinHandle = default;
            actualCapacity = 0;
            return null;
        }

        if ((long)capacity * sizeof(T) < PoolThresholdBytes)
        {
            T[] arr = ArrayPool<T>.Shared.Rent(capacity);
            GCHandle handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
            pooledArray = arr;
            pinHandle = handle;
            actualCapacity = arr.Length;
            return (T*)handle.AddrOfPinnedObject();
        }

        pooledArray = null;
        pinHandle = default;
        actualCapacity = capacity;
        if (UseAlignedAlloc)
            // Floor the alignment at sizeof(nuint): POSIX `aligned_alloc` requires the alignment
            // to be a multiple of sizeof(void*), so alignments of 1/2/4 on a 64-bit host (i.e.
            // T = byte/short/int/float) are undefined behavior even though glibc happens to
            // accept them. The resulting alignment is always at least the element size for
            // power-of-two T.
            return (T*)NativeMemory.AlignedAlloc((nuint)((long)capacity * sizeof(T)), (nuint)Math.Max(sizeof(T), sizeof(nuint)));
        return (T*)NativeMemory.Alloc((nuint)capacity, (nuint)sizeof(T));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FreeBuffer(T* ptr, T[]? pooledArray, GCHandle pinHandle)
    {
        if (pooledArray is not null)
        {
            if (pinHandle.IsAllocated) pinHandle.Free();
            ArrayPool<T>.Shared.Return(pooledArray);
        }
        else if (ptr is not null)
        {
            if (UseAlignedAlloc)
                NativeMemory.AlignedFree(ptr);
            else
                NativeMemory.Free(ptr);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GuardResize(
        ref T* ptr,
        ref int capacity,
        ref T[]? pooledArray,
        ref GCHandle pinHandle,
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

        T* newPtr = AllocateBuffer(newCapacity, out T[]? newPooled, out GCHandle newPin, out int newActual);
        if (count > 0)
        {
            Buffer.MemoryCopy(ptr, newPtr, (long)newActual * sizeof(T), (long)count * sizeof(T));
        }
        FreeBuffer(ptr, pooledArray, pinHandle);
        ptr = newPtr;
        pooledArray = newPooled;
        pinHandle = newPin;
        capacity = newActual;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(ref T* ptr, ref int capacity, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, T item)
    {
        GuardResize(ref ptr, ref capacity, ref pooledArray, ref pinHandle, count);
        ptr[count++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddRange(ref T* ptr, ref int capacity, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return;
        GuardResize(ref ptr, ref capacity, ref pooledArray, ref pinHandle, count, items.Length);
        items.CopyTo(new Span<T>(ptr + count, items.Length));
        count += items.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(ref int count) => count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReduceCount(ref T* ptr, ref int capacity, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, int newCount)
    {
        if (newCount == count) return;
        if (newCount > count) ThrowOnlyReduce(newCount, count);

        count = newCount;

        if (newCount < capacity / 2 && newCount > 0)
        {
            T* newPtr = AllocateBuffer(newCount, out T[]? newPooled, out GCHandle newPin, out int newActual);
            Buffer.MemoryCopy(ptr, newPtr, (long)newActual * sizeof(T), (long)newCount * sizeof(T));
            FreeBuffer(ptr, pooledArray, pinHandle);
            ptr = newPtr;
            pooledArray = newPooled;
            pinHandle = newPin;
            capacity = newActual;
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
    public static void Insert(ref T* ptr, ref int capacity, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, int index, T item)
    {
        GuardIndex(index, count, shouldThrow: true, allowEqualToCount: true);
        GuardResize(ref ptr, ref capacity, ref pooledArray, ref pinHandle, count);
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
    public static void Dispose(ref T* ptr, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, ref int capacity)
    {
        T* localPtr = ptr;
        T[]? localPool = pooledArray;
        GCHandle localPin = pinHandle;
        ptr = null;
        pooledArray = null;
        pinHandle = default;
        FreeBuffer(localPtr, localPool, localPin);
        count = 0;
        capacity = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Dispose(ref T* ptr, ref T[]? pooledArray, ref GCHandle pinHandle, ref int count, ref int capacity, ref bool disposed)
    {
        if (!disposed)
        {
            disposed = true;
            Dispose(ref ptr, ref pooledArray, ref pinHandle, ref count, ref capacity);
        }
    }
}
