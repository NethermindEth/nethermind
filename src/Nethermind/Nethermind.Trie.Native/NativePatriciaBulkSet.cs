// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private const string LibraryName = "nethermind_patricia";

    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(LibraryName, typeof(NativePatriciaBulkSet).Assembly, null, out _);

    /// <summary>
    /// Applies <paramref name="entries"/> to the trie whose root is stored at <see cref="TreePath.Empty"/> in <paramref name="store"/>,
    /// writing every new node referenced by hash, and returns the new root hash.
    /// </summary>
    /// <remarks>
    /// <paramref name="entries"/> is reordered in place and its keys must be unique. Hashing and node writes run in parallel on the native
    /// thread pool. Nodes that become unreachable are not removed, except the root when the trie becomes empty.
    /// </remarks>
    public static Hash256 BulkSet(INativeNodeStore store, Span<NativeBulkSetEntry> entries, ReadOnlySpan<byte> values)
    {
        CallState state = new(store);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            ValueHash256 root = default;
            int status;
            fixed (NativeBulkSetEntry* entriesPointer = entries)
            fixed (byte* valuesPointer = values)
            {
                status = nm_patricia_bulk_set(entriesPointer, entries.Length, valuesPointer, values.Length, &Fetch, &Write, (void*)GCHandle.ToIntPtr(handle), &root);
            }

            if (state.Exception is not null) throw new InvalidOperationException("Native node store callback failed", state.Exception);
            if (status != 0) throw new InvalidOperationException($"Native bulk set failed with status {status}");
            return new Hash256(root);
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int Fetch(void* context, TreePath* path, byte* buffer, int bufferLength)
    {
        CallState state = CallState.From(context);
        try
        {
            return state.Store.Fetch(in *path, new Span<byte>(buffer, bufferLength));
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref state.Exception, e, null);
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static void Write(void* context, TreePath* path, byte* data, int length)
    {
        CallState state = CallState.From(context);
        try
        {
            state.Store.Write(in *path, new ReadOnlySpan<byte>(data, length));
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref state.Exception, e, null);
        }
    }

    [LibraryImport(LibraryName)]
    private static partial int nm_patricia_bulk_set(
        NativeBulkSetEntry* entries,
        int entryCount,
        byte* values,
        int valuesLength,
        delegate* unmanaged<void*, TreePath*, byte*, int, int> fetch,
        delegate* unmanaged<void*, TreePath*, byte*, int, void> write,
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
