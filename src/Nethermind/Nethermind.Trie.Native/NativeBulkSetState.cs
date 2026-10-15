// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>An owned trie whose changed nodes remain unhashed until <see cref="NativePatriciaBulkSet.Finish"/>.</summary>
/// <remarks>
/// Use the same log for every update and finish, retaining the base trie's nonpersisted nodes through its normal generation pins.
/// Calls must not overlap, but ownership can move between threads. A failed update or any finish invalidates the contents;
/// disposal still frees the allocation. Disposal requires no live node store, log, or warmed cache.
/// </remarks>
public sealed unsafe partial class NativeBulkSetState : SafeHandle
{
    private NativeBulkSetState() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Creates a prepared trie from <paramref name="root"/>, for a storage account or the state trie when the address is null.</summary>
    public static NativeBulkSetState Create(in ValueHash256 root, Hash256? address)
    {
        NativeBulkSetState state = new();
        ValueHash256 addressValue = address?.ValueHash256 ?? default;
        // Native creation copies the root and address before these pinned/local values leave scope.
        fixed (ValueHash256* rootPointer = &root)
        {
            state.SetHandle(nm_patricia_bulk_state_new(rootPointer, address is null ? null : &addressValue));
        }
        if (state.IsInvalid) throw new InvalidOperationException("Native bulk set state creation failed");
        return state;
    }

    protected override bool ReleaseHandle()
    {
        nm_patricia_bulk_state_free(handle);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_bulk_state_new(ValueHash256* root, ValueHash256* address);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_bulk_state_free(nint state);
}
