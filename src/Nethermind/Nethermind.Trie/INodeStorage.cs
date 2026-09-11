// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

public interface INodeStorage
{
    byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None);
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

    public interface IWriteBatch : IDisposable
    {
        void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags);
    }
}
