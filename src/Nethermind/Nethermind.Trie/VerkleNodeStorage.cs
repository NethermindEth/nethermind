// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie;

public class VerkleNodeStorage(IColumnsDb<VerkleDbColumns> columns): INodeStorage
{
    public IDb LeafDb { get; } = columns.GetColumnDb(VerkleDbColumns.Leaf);
    public IDb InternalNodeDb { get; } = columns.GetColumnDb(VerkleDbColumns.InternalNodes);

    public INodeStorage.KeyScheme Scheme { get; set; } = INodeStorage.KeyScheme.Path;
    public bool RequirePath { get; } = true;
    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        throw new NotImplementedException();
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data,
        WriteFlags writeFlags = WriteFlags.None)
    {
        throw new NotImplementedException();
    }

    public INodeStorage.IWriteBatch StartWriteBatch()
    {
        var columnsWriteBatch = columns.StartWriteBatch();
        var leafNodeBatch = columnsWriteBatch.GetColumnBatch(VerkleDbColumns.Leaf);
        var internalNodeBatch = columnsWriteBatch.GetColumnBatch(VerkleDbColumns.InternalNodes);

        return new WriteBatch(leafNodeBatch, internalNodeBatch);
    }

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash)
    {
        throw new NotImplementedException();
    }

    public void Flush(bool onlyWal)
    {
        throw new NotImplementedException();
    }

    public void Compact()
    {
        throw new NotImplementedException();
    }

    private class WriteBatch(IWriteBatch leafWriteBatch, IWriteBatch internalNodesWriteBatch) : INodeStorage.IWriteBatch
    {
        public void Dispose()
        {
            leafWriteBatch.Dispose();
            internalNodesWriteBatch.Dispose();
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
        {
            if (keccak != Keccak.EmptyTreeHash.ValueHash256)
            {
                // write to the batch
            }
        }
    }
}
