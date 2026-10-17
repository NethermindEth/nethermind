// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes native bulk sets wrote, held natively in a log they read them back from in place.
/// </summary>
/// <remarks>
/// Each node references its children by hash and by their place in the log, and only trie roots are indexed: state roots
/// by hash, storage roots by address and hash. A trie is therefore found by its root and walked in place, so nodes of every
/// branch can be kept without being invalidated on reorgs. Holds nodes in a few small generations, dropping the oldest
/// when a new one starts, which is compacted in the background once no bulk set that started before is still running:
/// only the last node at each path is kept, unless a later generation likely rewrote that path. Compacted generations
/// take about <c>compressedMaxMemory</c> bytes, the oldest being freed beyond it once no running bulk set started before.
/// A <see cref="NativeTrieNodeLogPin"/> keeps its generation and every later one, so the log holds more while pinned.
/// </remarks>
public sealed unsafe partial class NativeTrieNodeLog : SafeHandle
{
    public NativeTrieNodeLog(ulong compressedMaxMemory) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(nm_patricia_log_new(compressedMaxMemory));

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

    /// <summary>The RLP and hash of the node whose record is at <paramref name="pointer"/>.</summary>
    /// <remarks>
    /// The RLP is read in place, so it stays valid only while a <see cref="NativeTrieNodeLogPin"/> keeps the generation
    /// of <paramref name="pointer"/>, as one taken before the node was written does.
    /// </remarks>
    public ReadOnlySpan<byte> GetNodeRlp(ulong pointer, out ValueHash256 hash)
    {
        ValueHash256 nodeHash = default;
        byte* rlp;
        int length = nm_patricia_log_node(this, pointer, &nodeHash, &rlp);
        if (length < 0) throw new InvalidOperationException($"Native trie node log read failed with status {length}");

        hash = nodeHash;
        return new ReadOnlySpan<byte>(rlp, length);
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_log_new(ulong compressedMaxMemory);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_log_node(NativeTrieNodeLog log, ulong pointer, ValueHash256* hashOut, byte** rlpOut);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial ulong nm_patricia_log_retain_active(NativeTrieNodeLog log);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_retain(NativeTrieNodeLog log, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_release(NativeTrieNodeLog log, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_log_free(nint log);
}
