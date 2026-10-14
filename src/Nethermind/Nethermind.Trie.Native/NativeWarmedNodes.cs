// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes <see cref="NativePatriciaBulkSet.WarmUp"/> loaded from a store, held natively, which native bulk
/// sets given them read in place instead of fetching.
/// </summary>
/// <remarks>Reset and dispose only once no warm-up nor bulk set uses them.</remarks>
public sealed partial class NativeWarmedNodes : SafeHandle
{
    public NativeWarmedNodes() : base(IntPtr.Zero, ownsHandle: true) => SetHandle(nm_patricia_warmed_new());

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Drops every node.</summary>
    public void Reset() => nm_patricia_warmed_reset(this);

    protected override bool ReleaseHandle()
    {
        nm_patricia_warmed_free(handle);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_warmed_new();

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_warmed_reset(NativeWarmedNodes warmed);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_warmed_free(nint warmed);
}
