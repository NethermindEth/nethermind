// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes native bulk sets wrote, held natively in a log they read them back from in place.
/// </summary>
/// <remarks>
/// Each node references its children by hash and by their place in the log, and only trie roots are indexed: state roots
/// by hash, storage roots by address and hash. A trie is therefore found by its root and walked in place, so nodes of every
/// branch can be kept without being invalidated on reorgs. Holds about <c>maxMemory</c> bytes of nodes in generations,
/// dropping the oldest when a new one starts, which is freed once no bulk set that started before is still running.
/// </remarks>
public sealed partial class NativeTrieNodeLog : SafeHandle
{
    public NativeTrieNodeLog(ulong maxMemory) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(nm_patricia_log_new(maxMemory));

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        nm_patricia_log_free(handle);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_log_new(ulong maxMemory);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_free(nint log);
}
