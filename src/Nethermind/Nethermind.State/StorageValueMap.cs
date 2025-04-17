// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.State;

/// <summary>
///
/// </summary>
internal sealed class StorageValueMap : IDisposable
{
    private const int StorageValuesCount = 1024 * 1024;
    private const int StorageValuesCountMask = StorageValuesCount - 1;
    private const UIntPtr StorageValuesSize = StorageValue.MemorySize * StorageValuesCount;
    private unsafe StorageValue* _values;
    private static readonly ConcurrentQueue<UIntPtr> Pool = new();
    private static readonly Channel<UIntPtr> Cleaner = Channel.CreateUnbounded<UIntPtr>();
    private static bool started;

    public unsafe StorageValueMap()
    {
        _values = Alloc();
    }

    private static unsafe StorageValue* Alloc()
    {
        if (Pool.TryDequeue(out var dequeued))
        {
            return (StorageValue*)dequeued.ToPointer();
        }

        var alloc = (StorageValue*)NativeMemory.AlignedAlloc(StorageValuesSize, StorageValue.MemorySize);
        GC.AddMemoryPressure((long)StorageValuesSize);

        Clean(new UIntPtr(alloc));

        return alloc;
    }

    private static unsafe void Clean(UIntPtr alloc)
    {
        NativeMemory.Clear(alloc.ToPointer(), StorageValuesSize);
    }

    [SkipLocalsInit]
    public unsafe Ptr Map(in StorageValue value)
    {
        if (value.IsZero)
            return Ptr.Null;

        var hash = value.GetHashCode();
        var values = _values;

        for (var i = 0; i < StorageValuesCount; i++)
        {
            var at = (hash + i) & StorageValuesCountMask;

            var ptr = new Ptr(values + at);

            if (ptr.Ref.Equals(value))
            {
                return ptr;
            }

            if (ptr.Ref.IsZero)
            {
                ptr.SetValue(value);
                return ptr;
            }
        }

        // Return null
        return default;
    }

    public unsafe void Clear()
    {
        ReturnValues();
        _values = Alloc();
    }

    private unsafe void ReturnValues()
    {
        if (Volatile.Read(ref started) == false)
        {
            TryStartCleaner();
        }

        while (Cleaner.Writer.TryWrite(new UIntPtr(_values)) == false)
        {
            // should always happen
            default(SpinWait).SpinOnce();
        }
    }

    public static bool IsCleaningQueueEmpty => Cleaner.Reader.TryPeek(out _) == false;

    private static void TryStartCleaner()
    {
        var startedBefore = Interlocked.Exchange(ref started, true);
        if (startedBefore == false)
        {
            Task.Factory.StartNew(async () =>
            {
                var reader = Cleaner.Reader;
                while (await reader.WaitToReadAsync())
                {
                    while (reader.TryRead(out var toClean))
                    {
                        Clean(toClean);
                        Pool.Enqueue(toClean);
                    }
                }
            });
        }
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

    private void ReleaseUnmanagedResources()
    {
        ReturnValues();
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
