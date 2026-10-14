// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes shared by native bulk sets, held natively and laid out like the managed trie node cache.
/// </summary>
/// <remarks>
/// A node is found by its trie and path and only returned when its hash matches too, so nodes of every branch can be
/// cached without being invalidated on reorgs. Holds about <c>maxMemory</c> bytes of nodes, clearing a shard of nearby
/// paths at a time when over it; a zero <c>maxMemory</c> caches nothing.
/// </remarks>
public sealed partial class NativeTrieNodeCache : SafeHandle
{
    public NativeTrieNodeCache(ulong maxMemory) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(nm_patricia_cache_new(maxMemory));

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        nm_patricia_cache_free(handle);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_cache_new(ulong maxMemory);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_cache_free(nint cache);
}
