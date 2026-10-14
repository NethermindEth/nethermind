// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    /// into <paramref name="pending"/>, and returns the new root hash. Nodes are read from <paramref name="pending"/> before <paramref name="store"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="entries"/> is reordered in place and its keys must be unique. Hashing and node writes run in parallel on the native
    /// thread pool. Nodes that become unreachable are not removed.
    /// </remarks>
    public static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, in ValueHash256 root, Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values)
    {
        CallState state = new(store);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            ValueHash256 newRoot = default;
            int status;
            fixed (NativeBulkSetEntry* entriesPointer = entries)
            fixed (byte* valuesPointer = values)
            fixed (ValueHash256* rootPointer = &root)
            {
                status = nm_patricia_bulk_set(entriesPointer, entries.Length, valuesPointer, values.Length, rootPointer, pending, &Fetch, (void*)GCHandle.ToIntPtr(handle), &newRoot);
            }

            if (state.Exception is not null) throw new InvalidOperationException("Native node store callback failed", state.Exception);
            if (status != 0) throw new InvalidOperationException($"Native bulk set failed with status {status}");
            return new Hash256(newRoot);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc cref="BulkSet(INativeNodeStore, NativePendingNodes, in ValueHash256, Span{NativeBulkSetEntry}, ReadOnlySpan{byte})"/>
    /// <remarks>A null or empty <see cref="PatriciaTree.BulkSetEntry.Value"/> deletes the key. Keys must be unique.</remarks>
    public static Hash256 BulkSet(INativeNodeStore store, NativePendingNodes pending, in ValueHash256 root, ReadOnlySpan<PatriciaTree.BulkSetEntry> entries)
    {
        int valuesLength = 0;
        foreach (PatriciaTree.BulkSetEntry entry in entries) valuesLength += entry.Value?.Length ?? 0;

        NativeBulkSetEntry[] nativeEntries = ArrayPool<NativeBulkSetEntry>.Shared.Rent(entries.Length);
        byte[] values = ArrayPool<byte>.Shared.Rent(valuesLength);
        try
        {
            int offset = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                ReadOnlySpan<byte> value = entries[i].Value;
                value.CopyTo(values.AsSpan(offset));
                nativeEntries[i] = new NativeBulkSetEntry(in entries[i].Path, offset, value.Length);
                offset += value.Length;
            }

            return BulkSet(store, pending, in root, nativeEntries.AsSpan(0, entries.Length), values.AsSpan(0, valuesLength));
        }
        finally
        {
            ArrayPool<NativeBulkSetEntry>.Shared.Return(nativeEntries);
            ArrayPool<byte>.Shared.Return(values);
        }
    }

    [UnmanagedCallersOnly]
    private static int Fetch(void* context, TreePath* path, ValueHash256* hash, byte* buffer, int bufferLength)
    {
        CallState state = CallState.From(context);
        try
        {
            return state.Store.Fetch(in *path, in *hash, new Span<byte>(buffer, bufferLength));
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref state.Exception, e, null);
            return -1;
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
        delegate* unmanaged<void*, TreePath*, ValueHash256*, byte*, int, int> fetch,
        void* context,
        ValueHash256* rootOut);

    private sealed class CallState(INativeNodeStore store)
    {
        public readonly INativeNodeStore Store = store;
        public Exception? Exception;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CallState From(void* context) => (CallState)GCHandle.FromIntPtr((nint)context).Target!;
    }
}
