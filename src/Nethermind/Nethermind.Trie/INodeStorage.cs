// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

public interface INodeStorage
{
    /// <summary>
    /// What is the current key scheme
    /// </summary>
    public KeyScheme Scheme { get; set; }

    /// <summary>
    /// When running completely from hash based db, some code path that calculate path can be ignored.
    /// </summary>
    public bool RequirePath { get; }

    byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None);

    /// <summary>Passes the RLP of the node <see cref="Get"/> returns to <paramref name="action"/>, borrowed for the duration of the call.</summary>
    /// <remarks><paramref name="action"/> runs synchronously, at most once, and only when the node is found.</remarks>
    /// <param name="address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
    /// <returns>Whether the node was found and passed.</returns>
    bool TryGet<TArg>(in ValueHash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags, ReadOnlySpanAction<byte, TArg> action, TArg arg)
        where TArg : allows ref struct
    {
        byte[]? rlp = Get(address is { } storage ? new Hash256(storage) : null, path, keccak, readFlags);
        if (rlp is null) return false;
        action(rlp, arg);
        return true;
    }

    void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None);
    IWriteBatch StartWriteBatch();

    /// <summary>
    /// Used by StateSync
    /// </summary>
    bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash);

    /// <summary>
    /// Used by StateSync to make sure values are flushed.
    /// </summary>
    /// <param name="onlyWal">True if only WAL file should be flushed, not memtable.</param>
    void Flush(bool onlyWal);
    void Compact();

    public enum KeyScheme
    {
        Hash,
        HalfPath,

        // The default setting in config, which for some reason, can't be a null enum.
        Current,
    }

    public interface IWriteBatch : IDisposable
    {
        void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags);
    }
}
