// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.Xdc;

/// <summary>
/// Exposes the state and storage trie nodes written to FlatDB's sync view as a read-only node store.
/// </summary>
internal sealed class XdcSyncNodeStorage(IPersistence.IPersistenceReader reader) : INodeStorage
{
    private static readonly byte[] EmptyTreeNode = [0x80];

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (keccak == Keccak.EmptyTreeHash.ValueHash256)
            return EmptyTreeNode;

        byte[]? rlp = address is null
            ? reader.TryLoadStateRlp(path, readFlags)
            : reader.TryLoadStorageRlp(address, path, readFlags);

        return rlp is not null && ValueKeccak.Compute(rlp) == keccak ? rlp : null;
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None) =>
        throw new InvalidOperationException("The XDC sync state store is read-only.");

    public INodeStorage.IWriteBatch StartWriteBatch() =>
        throw new InvalidOperationException("The XDC sync state store is read-only.");

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) =>
        Get(address is { } value ? value.ToHash256() : null, path, in hash) is not null;

    public void Flush(bool onlyWal) =>
        throw new InvalidOperationException("The XDC sync state store is read-only.");

    public void Compact() =>
        throw new InvalidOperationException("The XDC sync state store is read-only.");

    public bool HasRoot(Hash256 stateRoot) =>
        Get(null, TreePath.Empty, stateRoot.ValueHash256) is not null;
}
