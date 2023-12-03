// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

public interface INodeStorage
{
    public KeyScheme Scheme { get; }
    byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None);
    void Set(Hash256? address, in TreePath path, in ValueHash256 hash, byte[] toArray, WriteFlags writeFlags = WriteFlags.None);
    bool KeyExists(Hash256? address, in TreePath path, in ValueHash256 hash);
    WriteBatch StartWriteBatch();

    byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags);
    void Flush();

    public enum KeyScheme
    {
        Hash,
        HalfPath
    }

    public interface WriteBatch : IDisposable
    {
        void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, byte[] toArray, WriteFlags writeFlags);
    }
}
