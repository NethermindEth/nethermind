// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleKeyValueBatch(IColumnsWriteBatch<VerkleDbColumns> columnsWriteBatch) : IVerkleWriteOnlyDb, IDisposable
{
    private readonly IWriteBatch _leafNodeBatch = columnsWriteBatch.GetColumnBatch(VerkleDbColumns.Leaf);
    private readonly IWriteBatch _internalNodeBatch = columnsWriteBatch.GetColumnBatch(VerkleDbColumns.InternalNodes);

    private bool _isDisposed;

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        _leafNodeBatch[leafKey] = leafValue;
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        SetInternalNode(internalNodeKey, internalNodeValue, _internalNodeBatch);
    }

    private static void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode? internalNodeValue,
        IWriteOnlyKeyValueStore db)
    {
        if (internalNodeValue != null)
            db[internalNodeKey] = InternalNodeSerializer.Instance.Encode(internalNodeValue).Bytes;
    }

    public void RemoveLeaf(ReadOnlySpan<byte> leafKey)
    {
        _leafNodeBatch.Remove(leafKey);
    }

    public void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey)
    {
        _internalNodeBatch.Remove(internalNodeKey);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        columnsWriteBatch.Dispose();
    }
}

public class VerkleKeyValueDb(IColumnsDb<VerkleDbColumns> columns) : IVerkleDb, IVerkleKeyValueDb
{
    public VerkleKeyValueDb(IDbProvider dbProvider) : this(dbProvider.GetColumnDb<VerkleDbColumns>(DbNames.VerkleState))
    {
    }

    public bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value)
    {
        value = GetLeaf(key);
        return value is not null;
    }

    public bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode? value)
    {
        value = GetInternalNode(key);
        return value is not null;
    }

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        LeafDb[leafKey] = leafValue;
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        SetInternalNode(internalNodeKey, internalNodeValue, InternalNodeDb);
    }

    public void RemoveLeaf(ReadOnlySpan<byte> leafKey)
    {
        LeafDb.Remove(leafKey);
    }

    public void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey)
    {
        InternalNodeDb.Remove(internalNodeKey);
    }

    public VerkleKeyValueBatch StartWriteBatch()
    {
        return new VerkleKeyValueBatch(columns.StartWriteBatch());
    }

    public IDb LeafDb { get; } = columns.GetColumnDb(VerkleDbColumns.Leaf);
    public IDb InternalNodeDb { get; } = columns.GetColumnDb(VerkleDbColumns.InternalNodes);

    public byte[]? GetLeaf(ReadOnlySpan<byte> key)
    {
        return LeafDb.Get(key);
    }

    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key)
    {
        var value = InternalNodeDb[key];
        return value is null ? null : InternalNodeSerializer.Instance.Decode(value);
    }

    private static void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode? internalNodeValue,
        IWriteOnlyKeyValueStore db)
    {
        if (internalNodeValue != null)
            db[internalNodeKey] = InternalNodeSerializer.Instance.Encode(internalNodeValue).Bytes;
    }
}
