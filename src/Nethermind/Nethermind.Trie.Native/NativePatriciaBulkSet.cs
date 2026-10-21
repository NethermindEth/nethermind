// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>A key to set, with its value at <c>values[Offset..Offset + Length]</c>. A zero length deletes the key.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeBulkSetEntry(in ValueHash256 key, int offset, int length)
{
    public readonly ValueHash256 Key = key;
    public readonly int Offset = offset;
    public readonly int Length = length;
}

/// <summary>Patricia trie bulk set implemented in Rust (<c>rust/</c>) over an <see cref="INativeNodeStore"/>.</summary>
public static unsafe partial class NativePatriciaBulkSet
{
    internal const string LibraryName = "nethermind_patricia";

    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(LibraryName, typeof(NativePatriciaBulkSet).Assembly, null, out _);

    /// <summary>
    /// Applies <paramref name="entries"/> to the trie whose root hash is <paramref name="root"/>, writing every new node referenced by hash
    /// into <paramref name="pending"/>, and returns the new root hash. Every node written also goes into <paramref name="buffer"/>, or a
    /// temporary buffer when it is null, its root indexed as that of the storage trie of <paramref name="address"/>, or the state trie when it
    /// is null. Nodes reachable through that root in <paramref name="buffer"/> are read there, and the others from <paramref name="pending"/>,
    /// then <paramref name="warmed"/> unless it is null, then <paramref name="store"/>, without being added to <paramref name="buffer"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="entries"/> is reordered in place and its keys must be unique. Hashing and node writes run in parallel on the native
    /// thread pool. Nodes that become unreachable are not removed.
    /// </remarks>
    public static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, NativeNodeBuffer? buffer, NativeWarmedNodes? warmed, Hash256? address, in ValueHash256 root, Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values)
    {
        CallState state = new(store);
        GCHandle handle = GCHandle.Alloc(state);
        bool bufferReferenced = false;
        bool warmedReferenced = false;
        try
        {
            buffer?.DangerousAddRef(ref bufferReferenced);
            warmed?.DangerousAddRef(ref warmedReferenced);
            ValueHash256 newRoot = default;
            ValueHash256 addressValue = address?.ValueHash256 ?? default;
            int status;
            long start = Stopwatch.GetTimestamp();
            fixed (NativeBulkSetEntry* entriesPointer = entries)
            fixed (byte* valuesPointer = values)
            fixed (ValueHash256* rootPointer = &root)
            {
                status = nm_patricia_bulk_set(entriesPointer, entries.Length, valuesPointer, values.Length, rootPointer, pending,
                    bufferReferenced ? buffer!.DangerousGetHandle() : 0,
                    warmedReferenced ? warmed!.DangerousGetHandle() : 0,
                    address is null ? null : &addressValue, &Fetch, (void*)GCHandle.ToIntPtr(handle), &newRoot);
            }
            Metrics.AddBulkSetCall(Stopwatch.GetElapsedTime(start).Ticks, Stopwatch.GetElapsedTime(0, state.FetchTimestampTicks).Ticks);

            if (state.Exception is not null) throw new InvalidOperationException("Native node store callback failed", state.Exception);
            if (status != 0) throw new InvalidOperationException($"Native bulk set failed with status {status}");
            return new Hash256(newRoot);
        }
        finally
        {
            if (bufferReferenced) buffer!.DangerousRelease();
            if (warmedReferenced) warmed!.DangerousRelease();
            handle.Free();
        }
    }

    /// <inheritdoc cref="BulkSet(INativeNodeStore, NativePendingNodes, NativeNodeBuffer?, NativeWarmedNodes?, Hash256?, in ValueHash256, Span{NativeBulkSetEntry}, ReadOnlySpan{byte})"/>
    /// <remarks>A null or empty <see cref="PatriciaTree.BulkSetEntry.Value"/> deletes the key. Keys must be unique.</remarks>
    public static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, NativeNodeBuffer? buffer, NativeWarmedNodes? warmed, Hash256? address, in ValueHash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        long start = Stopwatch.GetTimestamp();
        int valuesLength = GetValuesLength(entries);

        NativeBulkSetEntry[] nativeEntries = ArrayPool<NativeBulkSetEntry>.Shared.Rent(entries.Length);
        byte[] values = ArrayPool<byte>.Shared.Rent(valuesLength);
        try
        {
            PackEntries(entries, nativeEntries, values);

            return BulkSet(store, pending, buffer, warmed, address, in root, nativeEntries.AsSpan(0, entries.Length), values.AsSpan(0, valuesLength));
        }
        finally
        {
            ArrayPool<NativeBulkSetEntry>.Shared.Return(nativeEntries);
            ArrayPool<byte>.Shared.Return(values);
            Metrics.AddBulkSet(Stopwatch.GetElapsedTime(start).Ticks);
        }
    }

    /// <summary>Applies another batch to an owned trie without hashing or writing encoded nodes.</summary>
    /// <remarks>
    /// Entries are reordered and must have unique keys. Values and callback state are used only during the call.
    /// Use the same buffer for every call on the trie, and do not reset warmed nodes during the call.
    /// An error invalidates the trie, which must be disposed.
    /// </remarks>
    public static void Update(INativeNodeStore store, NativeBulkSetState trie, NativeNodeBuffer buffer, NativeWarmedNodes? warmed,
        Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values) =>
        CallPrepared(store, trie, null, buffer, warmed, entries, values);

    /// <summary>Applies final corrections, hashes the prepared trie into pending nodes, and returns its root hash.</summary>
    /// <remarks>
    /// Finishes even with no corrections. Entries are reordered and must have unique keys. The trie is invalidated on success
    /// or failure, but must still be disposed. Do not publish pending output after a failure. Buffer and warmed-node lifetime
    /// requirements are the same as for <see cref="Update(INativeNodeStore, NativeBulkSetState, NativeNodeBuffer, NativeWarmedNodes?, Span{NativeBulkSetEntry}, ReadOnlySpan{byte})"/>.
    /// </remarks>
    public static Hash256 Finish(INativeNodeStore store, NativeBulkSetState trie, NativePendingNodes pending, NativeNodeBuffer buffer,
        NativeWarmedNodes? warmed, Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values) =>
        new(CallPrepared(store, trie, pending, buffer, warmed, entries, values));

    /// <inheritdoc cref="Update(INativeNodeStore, NativeBulkSetState, NativeNodeBuffer, NativeWarmedNodes?, Span{NativeBulkSetEntry}, ReadOnlySpan{byte})"/>
    /// <remarks>A null or empty value deletes the key. Keys must be unique.</remarks>
    public static void Update(INativeNodeStore store, NativeBulkSetState trie, NativeNodeBuffer buffer, NativeWarmedNodes? warmed,
        ReadOnlySpan<PatriciaTree.BulkSetEntry> entries) =>
        CallPrepared(store, trie, null, buffer, warmed, entries);

    /// <inheritdoc cref="Finish(INativeNodeStore, NativeBulkSetState, NativePendingNodes, NativeNodeBuffer, NativeWarmedNodes?, Span{NativeBulkSetEntry}, ReadOnlySpan{byte})"/>
    /// <remarks>A null or empty value deletes the key. Keys must be unique.</remarks>
    public static Hash256 Finish(INativeNodeStore store, NativeBulkSetState trie, NativePendingNodes pending, NativeNodeBuffer buffer,
        NativeWarmedNodes? warmed, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries) =>
        new(CallPrepared(store, trie, pending, buffer, warmed, entries));

    private static ValueHash256 CallPrepared(INativeNodeStore store, NativeBulkSetState trie, NativePendingNodes? pending,
        NativeNodeBuffer buffer, NativeWarmedNodes? warmed, Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values)
    {
        CallState state = new(store);
        GCHandle handle = GCHandle.Alloc(state);
        bool bufferReferenced = false;
        bool warmedReferenced = false;
        try
        {
            buffer.DangerousAddRef(ref bufferReferenced);
            warmed?.DangerousAddRef(ref warmedReferenced);
            ValueHash256 root = default;
            int status;
            long start = Stopwatch.GetTimestamp();
            // P/Invoke holds trie and pending handles; these pins and callback context live only for this call.
            fixed (NativeBulkSetEntry* entriesPointer = entries)
            fixed (byte* valuesPointer = values)
            {
                status = pending is null
                    ? nm_patricia_bulk_state_update(trie, entriesPointer, entries.Length, valuesPointer, values.Length,
                        buffer.DangerousGetHandle(), warmedReferenced ? warmed!.DangerousGetHandle() : 0, &Fetch, (void*)GCHandle.ToIntPtr(handle))
                    : nm_patricia_bulk_state_finish(trie, entriesPointer, entries.Length, valuesPointer, values.Length, pending,
                        buffer.DangerousGetHandle(), warmedReferenced ? warmed!.DangerousGetHandle() : 0, &Fetch, (void*)GCHandle.ToIntPtr(handle), &root);
            }
            Metrics.AddBulkSetCall(Stopwatch.GetElapsedTime(start).Ticks, Stopwatch.GetElapsedTime(0, state.FetchTimestampTicks).Ticks);

            if (state.Exception is not null) throw new InvalidOperationException("Native node store callback failed", state.Exception);
            if (status != 0) throw new InvalidOperationException($"Native bulk set failed with status {status}");
            return root;
        }
        finally
        {
            if (bufferReferenced) buffer.DangerousRelease();
            if (warmedReferenced) warmed!.DangerousRelease();
            handle.Free();
        }
    }

    private static ValueHash256 CallPrepared(INativeNodeStore store, NativeBulkSetState trie, NativePendingNodes? pending,
        NativeNodeBuffer buffer, NativeWarmedNodes? warmed, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        long start = Stopwatch.GetTimestamp();
        int valuesLength = GetValuesLength(entries);

        NativeBulkSetEntry[] nativeEntries = ArrayPool<NativeBulkSetEntry>.Shared.Rent(entries.Length);
        byte[] values = ArrayPool<byte>.Shared.Rent(valuesLength);
        try
        {
            PackEntries(entries, nativeEntries, values);

            return CallPrepared(store, trie, pending, buffer, warmed, nativeEntries.AsSpan(0, entries.Length), values.AsSpan(0, valuesLength));
        }
        finally
        {
            ArrayPool<NativeBulkSetEntry>.Shared.Return(nativeEntries);
            ArrayPool<byte>.Shared.Return(values);
            Metrics.AddBulkSet(Stopwatch.GetElapsedTime(start).Ticks);
        }
    }

    private static int GetValuesLength(ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        int length = 0;
        foreach (PatriciaTree.BulkSetEntry entry in entries) length += entry.Value?.Length ?? 0;
        return length;
    }

    private static void PackEntries(ReadOnlySpan<PatriciaTree.BulkSetEntry> entries, NativeBulkSetEntry[] nativeEntries, byte[] values)
    {
        int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlySpan<byte> value = entries[i].Value;
            value.CopyTo(values.AsSpan(offset));
            nativeEntries[i] = new NativeBulkSetEntry(in entries[i].Path, offset, value.Length);
            offset += value.Length;
        }
    }

    /// <summary>
    /// Warms the path to <paramref name="key"/> in the trie with root hash <paramref name="root"/>, the storage trie of
    /// <paramref name="address"/>, or the state trie when it is null: every node on it that native bulk sets cannot read
    /// from <paramref name="buffer"/> nor <paramref name="warmed"/> is fetched from <paramref name="store"/> and added to
    /// <paramref name="warmed"/>. Stops at the first node <paramref name="store"/> does not have.
    /// </summary>
    /// <remarks>Concurrent warm-ups and bulk sets may share <paramref name="warmed"/>.</remarks>
    /// <param name="forDelete">Also warms the sibling needed when deleting a matching leaf collapses a two-child branch.</param>
    public static void WarmUp(INativeNodeStore store, NativeNodeBuffer buffer, NativeWarmedNodes warmed, Hash256? address, in ValueHash256 root, in ValueHash256 key, bool forDelete = false)
    {
        CallState state = new(store);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            ValueHash256 addressValue = address?.ValueHash256 ?? default;
            int status;
            fixed (ValueHash256* rootPointer = &root)
            fixed (ValueHash256* keyPointer = &key)
            {
                status = nm_patricia_warm_up(buffer, warmed, address is null ? null : &addressValue, rootPointer, keyPointer, &Fetch, (void*)GCHandle.ToIntPtr(handle), forDelete);
            }

            if (state.Exception is not null) throw new InvalidOperationException("Native node store callback failed", state.Exception);
            if (status != 0) throw new InvalidOperationException($"Native warm-up failed with status {status}");
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int Fetch(void* context, TreePath* path, ValueHash256* hash, delegate* unmanaged<void*, byte*, int, void> receive, void* receiveContext)
    {
        CallState state = CallState.From(context);
        long start = Stopwatch.GetTimestamp();
        try
        {
            return state.Store.Fetch(in *path, in *hash, new NativeNodeReceiver(receive, receiveContext)) ? 1 : 0;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref state.Exception, e, null);
            return -1;
        }
        finally
        {
            Interlocked.Add(ref state.FetchTimestampTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    [LibraryImport(LibraryName)]
    private static partial int nm_patricia_bulk_set(
        NativeBulkSetEntry* entries,
        int entryCount,
        byte* values,
        int valuesLength,
        ValueHash256* root,
        NativePendingNodes pending,
        nint buffer,
        nint warmed,
        ValueHash256* address,
        delegate* unmanaged<void*, TreePath*, ValueHash256*, delegate* unmanaged<void*, byte*, int, void>, void*, int> fetch,
        void* context,
        ValueHash256* rootOut);

    [LibraryImport(LibraryName)]
    private static partial int nm_patricia_bulk_state_update(
        NativeBulkSetState state,
        NativeBulkSetEntry* entries,
        int entryCount,
        byte* values,
        int valuesLength,
        nint buffer,
        nint warmed,
        delegate* unmanaged<void*, TreePath*, ValueHash256*, delegate* unmanaged<void*, byte*, int, void>, void*, int> fetch,
        void* context);

    [LibraryImport(LibraryName)]
    private static partial int nm_patricia_bulk_state_finish(
        NativeBulkSetState state,
        NativeBulkSetEntry* entries,
        int entryCount,
        byte* values,
        int valuesLength,
        NativePendingNodes pending,
        nint buffer,
        nint warmed,
        delegate* unmanaged<void*, TreePath*, ValueHash256*, delegate* unmanaged<void*, byte*, int, void>, void*, int> fetch,
        void* context,
        ValueHash256* rootOut);

    [LibraryImport(LibraryName)]
    private static partial int nm_patricia_warm_up(
        NativeNodeBuffer buffer,
        NativeWarmedNodes warmed,
        ValueHash256* address,
        ValueHash256* root,
        ValueHash256* key,
        delegate* unmanaged<void*, TreePath*, ValueHash256*, delegate* unmanaged<void*, byte*, int, void>, void*, int> fetch,
        void* context,
        [MarshalAs(UnmanagedType.I1)] bool forDelete);

    private sealed class CallState(INativeNodeStore store)
    {
        public readonly INativeNodeStore Store = store;
        public Exception? Exception;
        // In Stopwatch timestamp units, summed over the threads calling back.
        public long FetchTimestampTicks;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CallState From(void* context) => (CallState)GCHandle.FromIntPtr((nint)context).Target!;
    }
}
