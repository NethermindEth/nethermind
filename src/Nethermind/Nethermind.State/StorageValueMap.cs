// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
/// Provides a <see cref="StorageValue"/> to a pointer mapping.
/// </summary>
/// <remarks>
/// The implementation is based on a version (epochs) hash map that provides a fast cleanup
/// after paying 4MB of the book keeping cost.
/// </remarks>
internal sealed class StorageValueMap : IDisposable
{
    private const int DefaultSize = 1024 * 1024;
    private UIntPtr StorageValuesSize => StorageValue.MemorySize * _size;
    private UIntPtr EpochsSize => sizeof(int) * _size;

    private unsafe StorageValue* _values;
    private unsafe int* _epochs;

    private int _epoch = 1;
    private readonly uint _size;

    public StorageValueMap(uint size = DefaultSize)
    {
        Debug.Assert(BitOperations.IsPow2(size));

        _size = size;
    }

    [SkipLocalsInit]
    public unsafe Ptr Map(in StorageValue value)
    {
        if (value.IsZero)
            return Ptr.Null;

        var hash = value.GetHashCode();
        if (_values == null)
        {
            Alloc();
        }

        Debug.Assert(_values != null);

        var values = _values;
        var epochs = _epochs;
        var mask = _size - 1;

        for (var i = 0; i < DefaultSize; i++)
        {
            var at = (hash + i) & mask;
            var ptr = new Ptr(values + at);

            ref var epoch = ref epochs[at];

            if (epoch == _epoch)
            {
                // The entry was written in this epoch. Check if it's the mapped one.
                if (ptr.Ref.Equals(value))
                {
                    return ptr;
                }
            }
            else
            {
                Debug.Assert(epoch < _epoch, "Entry should be from the past");

                // The entry was used in the past. Set the value and immediately return.
                epoch = _epoch;
                ptr.SetValue(value);
                return ptr;
            }
        }

        // Return null
        return default;
    }

    public void Clear()
    {
        _epoch++;
    }

    private unsafe void Alloc()
    {
        _values = (StorageValue*)NativeMemory.AlignedAlloc(StorageValuesSize, StorageValue.MemorySize);
        GC.AddMemoryPressure((long)StorageValuesSize);

        _epochs = (int*)NativeMemory.AlignedAlloc(EpochsSize, sizeof(int));
        NativeMemory.Clear(_epochs, EpochsSize);
        GC.AddMemoryPressure((long)EpochsSize);
    }

    private unsafe void Free()
    {
        if (_values != null)
        {
            NativeMemory.AlignedFree(_values);
            GC.RemoveMemoryPressure((long)StorageValuesSize);
        }

        if (_epochs != null)
        {
            NativeMemory.AlignedFree(_epochs);
            GC.RemoveMemoryPressure((long)EpochsSize);
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

    /// <summary>
    /// A not so managed reference to <see cref="StorageValue"/>.
    /// Allows quick equality checks and ref returning semantics.
    /// </summary>
    internal readonly unsafe struct Ptr : IEquatable<Ptr>
    {
        private readonly StorageValue* _pointer;

        public Ptr(StorageValue* pointer)
        {
            _pointer = pointer;
        }

        public bool IsZero => _pointer == null;

        public static readonly Ptr Null = default;

        public ref readonly StorageValue Ref =>
            ref _pointer == null ? ref StorageValue.Zero : ref Unsafe.AsRef<StorageValue>(_pointer);

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            return obj is Ptr other && Equals(other);
        }

        public bool Equals(Ptr other) => _pointer == other._pointer;

        public override int GetHashCode() => unchecked((int)(long)_pointer);

        public override string ToString()
        {
            return IsZero ? "0" : $"{Ref} @ {new UIntPtr(_pointer)}";
        }

        public void SetValue(in StorageValue value)
        {
            *_pointer = value;
        }
    }
}
