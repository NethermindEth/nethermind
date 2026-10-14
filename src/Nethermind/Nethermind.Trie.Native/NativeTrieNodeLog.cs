// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes native bulk sets wrote, held natively in a log they read them back from in place.
/// </summary>
/// <remarks>
/// Each node references its children by hash and by their place in the log, and only trie roots are indexed: state roots
/// by hash, storage roots by address and hash. A trie is therefore found by its root and walked in place, so nodes of every
/// branch can be kept without being invalidated on reorgs. Holds about <c>maxMemory</c> bytes of nodes in generations,
/// dropping the oldest when a new one starts, which is freed once no bulk set that started before is still running.
/// A <see cref="NativeTrieNodeLogPin"/> keeps its generation and every later one, so the log holds more while pinned.
/// </remarks>
public sealed unsafe partial class NativeTrieNodeLog : SafeHandle
{
    public NativeTrieNodeLog(ulong maxMemory) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(nm_patricia_log_new(maxMemory));

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        nm_patricia_log_free(handle);
        return true;
    }

    /// <summary>
    /// Pins the generation nodes are written to now, so native bulk sets started before the returned pin is disposed
    /// read every node written from now on from this log, reached from its trie's root.
    /// </summary>
    public NativeTrieNodeLogPin PinActiveGeneration()
    {
        bool referenced = false;
        DangerousAddRef(ref referenced);
        return new NativeTrieNodeLogPin(this, nm_patricia_log_retain_active(this));
    }

    internal NativeTrieNodeLogPin Pin(ulong generation)
    {
        bool referenced = false;
        DangerousAddRef(ref referenced);
        nm_patricia_log_retain(this, generation);
        return new NativeTrieNodeLogPin(this, generation);
    }

    internal void Unpin(ulong generation)
    {
        nm_patricia_log_release(this, generation);
        DangerousRelease();
    }

    /// <summary>
    /// Finds the first node on the path to <paramref name="key"/> in the trie with root hash <paramref name="root"/> that
    /// native bulk sets cannot read from this log, in the storage trie of <paramref name="address"/>, or the state trie
    /// when it is null.
    /// </summary>
    /// <param name="path">The path of that node.</param>
    /// <param name="hash">The hash of that node.</param>
    /// <returns>Whether there is such a node, false when bulk sets read every node on the path from this log.</returns>
    public bool TryFindFirstMissingNode(Hash256? address, in ValueHash256 root, in ValueHash256 key, out TreePath path, [NotNullWhen(true)] out Hash256? hash)
    {
        ValueHash256 addressValue = address?.ValueHash256 ?? default;
        ValueHash256 missingHash = default;
        int depth;
        fixed (ValueHash256* rootPointer = &root)
        fixed (ValueHash256* keyPointer = &key)
        {
            depth = nm_patricia_log_first_missing(this, address is null ? null : &addressValue, rootPointer, keyPointer, &missingHash);
        }

        if (depth < -1) throw new InvalidOperationException($"Native trie node log lookup failed with status {depth}");
        if (depth == -1)
        {
            path = default;
            hash = null;
            return false;
        }

        path = TreePath.FromPath(key.Bytes).Truncate(depth);
        hash = new Hash256(missingHash);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_log_new(ulong maxMemory);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_log_first_missing(NativeTrieNodeLog log, ValueHash256* address, ValueHash256* root, ValueHash256* key, ValueHash256* hashOut);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial ulong nm_patricia_log_retain_active(NativeTrieNodeLog log);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_retain(NativeTrieNodeLog log, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_release(NativeTrieNodeLog log, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_free(nint log);
}
