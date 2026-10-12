// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// The managed key-value backend of a native store: callbacks serving its reads, snapshots, iterators and batches
/// from a C# <see cref="IColumnsDb{TKey}"/> that is not RocksDB, such as the tests' in-memory columns.
/// </summary>
/// <remarks>
/// The callbacks follow native/crates/flatdb-core/src/kv/managed.rs. They may run on any thread, native threads of
/// the store included (its reader cache releases snapshots from a timer), and never throw into native code: an
/// exception becomes a failure status and is kept on its thread, for the call that failed to rethrow.
/// </remarks>
internal sealed unsafe class ManagedKvBridge : IDisposable
{
    private const int Succeeded = 0;
    private const int Failed = -1;
    private const int HasEntry = 1;
    private const int Exhausted = 0;

    private const byte OpPut = 0;
    private const byte OpDelete = 1;

    [ThreadStatic]
    private static Exception? _callbackException;

    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly IDb[] _columns;
    private readonly ConcurrentDictionary<ulong, IColumnDbSnapshot<FlatDbColumns>> _snapshots = new();
    private readonly ConcurrentDictionary<ulong, Iterator> _iterators = new();
    private GCHandle _self;
    private long _lastHandle;

    public ManagedKvBridge(IColumnsDb<FlatDbColumns> db)
    {
        _db = db;
        _columns = [.. Enum.GetValues<FlatDbColumns>().Select(db.GetColumnDb)];
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Snapshots and iterators the native store has not released.</summary>
    public int OpenHandles => _snapshots.Count + _iterators.Count;

    /// <summary>The callbacks; the native store copies them, and calls them until it is destroyed.</summary>
    public ManagedKvVTable VTable => new()
    {
        Ctx = GCHandle.ToIntPtr(_self),
        Get = (nint)(delegate* unmanaged<nint, ulong, uint, byte*, nuint, byte*, nuint, long*, int>)&Get,
        SnapshotCreate = (nint)(delegate* unmanaged<nint, ulong*, int>)&SnapshotCreate,
        SnapshotRelease = (nint)(delegate* unmanaged<nint, ulong, void>)&SnapshotRelease,
        IteratorCreate = (nint)(delegate* unmanaged<nint, ulong, uint, byte*, nuint, byte*, nuint, ulong*, int>)&IteratorCreate,
        IteratorNext = (nint)(delegate* unmanaged<nint, ulong, byte**, nuint*, byte**, nuint*, int>)&IteratorNext,
        IteratorRelease = (nint)(delegate* unmanaged<nint, ulong, void>)&IteratorRelease,
        Write = (nint)(delegate* unmanaged<nint, byte*, nuint, uint, int>)&Write,
        Flush = (nint)(delegate* unmanaged<nint, byte, int>)&Flush,
        SetWriteBuffer = (nint)(delegate* unmanaged<nint, uint, ulong, int>)&SetWriteBuffer,
    };

    /// <summary>Takes the exception a callback threw on this thread, if any.</summary>
    public static Exception? TakeCallbackException()
    {
        Exception? exception = _callbackException;
        _callbackException = null;
        return exception;
    }

    /// <summary>Frees the bridge once the native store is destroyed, which releases every snapshot and iterator first.</summary>
    public void Dispose()
    {
        if (_self.IsAllocated) _self.Free();
    }

    private static ManagedKvBridge Of(nint ctx) => (ManagedKvBridge)GCHandle.FromIntPtr(ctx).Target!;

    private static int Fail(Exception exception)
    {
        _callbackException = exception;
        return Failed;
    }

    private ulong NextHandle() => (ulong)Interlocked.Increment(ref _lastHandle);

    /// <summary>The column at <paramref name="snapshot"/>, or of the live database for 0.</summary>
    private IReadOnlyKeyValueStore Column(ulong snapshot, uint column) =>
        snapshot == 0 ? _columns[column] : _snapshots[snapshot].GetColumn((FlatDbColumns)column);

    [UnmanagedCallersOnly]
    private static int Get(nint ctx, ulong snapshot, uint column, byte* key, nuint keyLength, byte* buffer, nuint capacity, long* valueLength)
    {
        try
        {
            byte[]? value = Of(ctx).Column(snapshot, column).Get(new ReadOnlySpan<byte>(key, (int)keyLength));
            *valueLength = value?.Length ?? -1;
            if (value is not null && (nuint)value.Length <= capacity) value.CopyTo(new Span<byte>(buffer, value.Length));
            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static int SnapshotCreate(nint ctx, ulong* snapshot)
    {
        try
        {
            ManagedKvBridge bridge = Of(ctx);
            ulong handle = bridge.NextHandle();
            bridge._snapshots[handle] = bridge._db.CreateSnapshot();
            *snapshot = handle;
            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static void SnapshotRelease(nint ctx, ulong snapshot)
    {
        try
        {
            if (Of(ctx)._snapshots.TryRemove(snapshot, out IColumnDbSnapshot<FlatDbColumns>? released)) released.Dispose();
        }
        catch (Exception exception)
        {
            // Released from native code that has no status to return.
            _callbackException = exception;
        }
    }

    [UnmanagedCallersOnly]
    private static int IteratorCreate(nint ctx, ulong snapshot, uint column, byte* lower, nuint lowerLength, byte* upper, nuint upperLength, ulong* iterator)
    {
        try
        {
            ManagedKvBridge bridge = Of(ctx);
            ISortedKeyValueStore store = (ISortedKeyValueStore)bridge.Column(snapshot, column);

            // A null bound is unbounded: every key sorts after the empty one, and the successor of the last key is
            // past every key.
            ReadOnlySpan<byte> lowerBound = lower is null ? default : new ReadOnlySpan<byte>(lower, (int)lowerLength);
            byte[]? upperBound = upper is null ? Successor(store.LastKey) : new ReadOnlySpan<byte>(upper, (int)upperLength).ToArray();

            ulong handle = bridge.NextHandle();
            bridge._iterators[handle] = new Iterator(upperBound is null ? null : store.GetViewBetween(lowerBound, upperBound));
            *iterator = handle;
            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }

        static byte[]? Successor(byte[]? key) => key is null ? null : [.. key, 0];
    }

    [UnmanagedCallersOnly]
    private static int IteratorNext(nint ctx, ulong iterator, byte** key, nuint* keyLength, byte** value, nuint* valueLength)
    {
        try
        {
            return Of(ctx)._iterators[iterator].MoveNext(key, keyLength, value, valueLength) ? HasEntry : Exhausted;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static void IteratorRelease(nint ctx, ulong iterator)
    {
        try
        {
            if (Of(ctx)._iterators.TryRemove(iterator, out Iterator? released)) released.Dispose();
        }
        catch (Exception exception)
        {
            _callbackException = exception;
        }
    }

    /// <summary>Applies a serialized batch: tag, column, then <c>u32</c>-length-prefixed key and value.</summary>
    [UnmanagedCallersOnly]
    private static int Write(nint ctx, byte* operations, nuint length, uint flags)
    {
        try
        {
            WriteFlags writeFlags = (WriteFlags)flags;
            ReadOnlySpan<byte> stream = new(operations, (int)length);
            using IColumnsWriteBatch<FlatDbColumns> batch = Of(ctx)._db.StartWriteBatch();
            while (!stream.IsEmpty)
            {
                byte tag = stream[0];
                IWriteBatch column = batch.GetColumnBatch((FlatDbColumns)stream[1]);
                stream = stream[2..];
                ReadOnlySpan<byte> key = Part(ref stream);
                switch (tag)
                {
                    case OpPut:
                        column.PutSpan(key, Part(ref stream), writeFlags);
                        break;
                    case OpDelete:
                        column.Set(key, null, writeFlags);
                        break;
                    default:
                        throw new NotSupportedException($"The managed key-value backend does not apply operation {tag}.");
                }
            }

            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }

        static ReadOnlySpan<byte> Part(ref ReadOnlySpan<byte> stream)
        {
            int partLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream);
            ReadOnlySpan<byte> part = stream.Slice(sizeof(uint), partLength);
            stream = stream[(sizeof(uint) + partLength)..];
            return part;
        }
    }

    [UnmanagedCallersOnly]
    private static int Flush(nint ctx, byte onlyWal)
    {
        try
        {
            Of(ctx)._db.Flush(onlyWal != 0);
            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static int SetWriteBuffer(nint ctx, uint column, ulong sizeBytes)
    {
        try
        {
            Of(ctx)._columns[column].SetWriteBuffer((long)sizeBytes);
            return Succeeded;
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    /// <summary>A view whose current entry is copied to pinned memory, which native code reads until the next move.</summary>
    private sealed class Iterator(ISortedView? view) : IDisposable
    {
        private byte[] _entry = GC.AllocateUninitializedArray<byte>(256, pinned: true);

        public bool MoveNext(byte** key, nuint* keyLength, byte** value, nuint* valueLength)
        {
            if (view is null || !view.MoveNext()) return false;

            ReadOnlySpan<byte> currentKey = view.CurrentKey;
            ReadOnlySpan<byte> currentValue = view.CurrentValue;
            int size = currentKey.Length + currentValue.Length;
            if (size > _entry.Length) _entry = GC.AllocateUninitializedArray<byte>(size, pinned: true);

            currentKey.CopyTo(_entry);
            currentValue.CopyTo(_entry.AsSpan(currentKey.Length));
            byte* entry = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_entry));
            *key = entry;
            *keyLength = (nuint)currentKey.Length;
            *value = entry + currentKey.Length;
            *valueLength = (nuint)currentValue.Length;
            return true;
        }

        public void Dispose() => view?.Dispose();
    }
}
