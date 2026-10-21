// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

public sealed partial class NativeNodeBuffer
{
    /// <summary>Finds an indexed root of the state trie, or the storage trie of <paramref name="address"/>.</summary>
    /// <returns>The root's buffer pointer, or zero when the root is not available.</returns>
    public unsafe ulong ResolveRoot(Hash256? address, in ValueHash256 rootHash)
    {
        ValueHash256 addressHash = address?.ValueHash256 ?? default;
        fixed (ValueHash256* hash = &rootHash)
        {
            return nm_patricia_buffer_root(this, address is null ? null : &addressHash, hash);
        }
    }

    /// <summary>Copies the hash-referenced node at exactly <paramref name="path"/> under <paramref name="root"/>.</summary>
    /// <remarks>A native read session protects inherited and compacted records until their RLP has been copied.</remarks>
    public bool TryGetNode(ulong root, in TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        Span<byte> buffer = stackalloc byte[1024];
        int length = CopyNode(root, path, buffer, out ValueHash256 hash);
        if (length > buffer.Length)
        {
            buffer = new byte[length];
            length = CopyNode(root, path, buffer, out hash);
        }
        if (length < 0) throw new InvalidOperationException($"Native trie node lookup failed with status {length}");
        node = length == 0 ? null : new TrieNode(NodeType.Unknown, new Hash256(hash), new CappedArray<byte>(buffer[..length].ToArray()));
        return node is not null;
    }

    // The FFI copies from a session-protected record into this pinned caller-owned span; no borrowed pointer escapes.
    private unsafe int CopyNode(ulong root, in TreePath path, Span<byte> buffer, out ValueHash256 hash)
    {
        ValueHash256 copiedHash = default;
        fixed (TreePath* pathPointer = &path)
        fixed (byte* output = buffer)
        {
            int length = nm_patricia_buffer_find_node(this, root, pathPointer, &copiedHash, output, (nuint)buffer.Length);
            hash = copiedHash;
            return length;
        }
    }

    /// <summary>Enumerates reachable hash-referenced nodes written at or after <paramref name="writeBoundary"/>.</summary>
    /// <remarks>
    /// The boundary's pin must remain alive when enumeration starts. The cursor acquires its own retain and keeps the buffer
    /// alive until disposed. Returned pointers are readable through <see cref="GetNodeRlp"/> while a covering pin is held.
    /// </remarks>
    public IEnumerable<(TreePath Path, ulong Pointer)> EnumerateNodes(ulong root, ulong writeBoundary)
    {
        if (root == 0 || root < writeBoundary) yield break;
        using NativeNodeCursor cursor = new(this, root, writeBoundary);
        while (cursor.TryNext(out TreePath path, out ulong pointer)) yield return (path, pointer);
    }

    private sealed partial class NativeNodeCursor : SafeHandle
    {
        private readonly NativeNodeBuffer _buffer;

        public NativeNodeCursor(NativeNodeBuffer buffer, ulong root, ulong boundary) : base(IntPtr.Zero, ownsHandle: true)
        {
            _buffer = buffer;
            bool referenced = false;
            buffer.DangerousAddRef(ref referenced);
            try
            {
                SetHandle(nm_patricia_buffer_cursor_new(buffer, root, boundary));
                if (IsInvalid) throw new InvalidOperationException("Native trie node cursor creation failed");
            }
            catch
            {
                if (referenced) buffer.DangerousRelease();
                throw;
            }
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        // The output pointers refer only to pinned local values, and the SafeHandle pins the cursor throughout the call.
        public unsafe bool TryNext(out TreePath path, out ulong pointer)
        {
            TreePath nextPath = default;
            ulong nextPointer = 0;
            int status = nm_patricia_buffer_cursor_next(this, &nextPath, &nextPointer);
            if (status < 0) throw new InvalidOperationException($"Native trie node enumeration failed with status {status}");
            path = nextPath;
            pointer = nextPointer;
            return status != 0;
        }

        protected override bool ReleaseHandle()
        {
            nm_patricia_buffer_cursor_free(handle);
            _buffer.DangerousRelease();
            return true;
        }

        [LibraryImport(NativePatriciaBulkSet.LibraryName)]
        private static partial nint nm_patricia_buffer_cursor_new(NativeNodeBuffer buffer, ulong root, ulong boundary);

        [LibraryImport(NativePatriciaBulkSet.LibraryName)]
        private static unsafe partial int nm_patricia_buffer_cursor_next(NativeNodeCursor cursor, TreePath* path, ulong* pointer);

        [LibraryImport(NativePatriciaBulkSet.LibraryName)]
        private static partial void nm_patricia_buffer_cursor_free(nint cursor);
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static unsafe partial ulong nm_patricia_buffer_root(NativeNodeBuffer buffer, ValueHash256* address, ValueHash256* root);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static unsafe partial int nm_patricia_buffer_find_node(NativeNodeBuffer buffer, ulong root, TreePath* path, ValueHash256* hash, byte* output, nuint capacity);
}
