// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
/// Provides a <see cref="StorageValue"/> to a pointer mapping.
/// This allows keeping a 32-byte storage value hidden behind a pointer-size structure of <see cref="StorageValuePtr"/>,
/// which consumes much less memory.
/// </summary>
/// <remarks>
/// The implementation is based on a version (epochs) hash map that provides a fast cleanup
/// after paying 4MB of the book keeping cost.
/// </remarks>
internal sealed class StorageValueMap : IDisposable
{
    private const int DefaultSize = 1024 * 1024;
    private UIntPtr StorageValuesSize => StorageValue.MemorySize * _size;

    private readonly unsafe StorageValue* _values;
    private readonly int[] _epochs;

    private const int ReservedEpoch = int.MaxValue;
    private int _epoch = 1;

    private readonly uint _size;

    public unsafe StorageValueMap(uint size = DefaultSize)
    {
        Debug.Assert(BitOperations.IsPow2(size));

        _size = size;

        _values = (StorageValue*)NativeMemory.AlignedAlloc(StorageValuesSize, StorageValue.MemorySize);
        GC.AddMemoryPressure((long)StorageValuesSize);

        _epochs = new int[size];
    }

    [SkipLocalsInit]
    public unsafe StorageValuePtr Map(in StorageValue value)
    {
        if (value.IsZero)
            goto Nothing;

        var hash = value.GetHashCode();

        var mask = _size - 1;
        var i = 0;
        StorageValuePtr ptr;

        do
        {
            var at = (hash + i) & mask;

            ptr = new StorageValuePtr(_values + at);

            ref var epoch = ref _epochs[at];

            var read = Volatile.Read(ref epoch);
            if (read == _epoch)
            {
                // The entry was written in this epoch. Check if it's the mapped one.
                if (ptr.Ref.Equals(value))
                {
                    goto Return;
                }

                // Move to the next one, not equal
                i++;
                continue;
            }

            if (read == ReservedEpoch)
            {
                // Spin once again as this slot is reserved.
                // If there's a chance that a thread aborts in this place,
                // it should be considered migrating to negative epochs for locking as they can be recovered later.
                Thread.SpinWait(1);
                continue;
            }

            Debug.Assert(read < _epoch, "The read epoch should be from the past");

            // Try to lock it.
            if (Interlocked.CompareExchange(ref epoch, ReservedEpoch, read) == read)
            {
                ptr.SetValue(value);

                // Commit and unlock
                Volatile.Write(ref epoch, _epoch);
                goto Return;
            }

            // Didn't lock, retry
        } while (i < _size);

    Nothing:
        ptr = default;

    Return:
        return ptr;
    }

    public void Clear()
    {
        _epoch++;
    }

    private unsafe void Free()
    {
        if (_values != null)
        {
            NativeMemory.AlignedFree(_values);
            GC.RemoveMemoryPressure((long)StorageValuesSize);
        }
    }

    private void ReleaseUnmanagedResources()
    {
        Free();
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~StorageValueMap()
    {
        ReleaseUnmanagedResources();
    }
}
