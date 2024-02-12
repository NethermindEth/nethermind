// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleKeyValueBatch(IWriteBatch leafNodeBatch, IWriteBatch internalNodeBatch) : IVerkleWriteOnlyDb, IDisposable
{
    private bool _isDisposed;

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        leafNodeBatch[leafKey] = leafValue;
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        SetInternalNode(internalNodeKey, internalNodeValue, internalNodeBatch);
    }

    private static void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode? internalNodeValue,
        IWriteOnlyKeyValueStore db)
    {
        if (internalNodeValue != null)
            db[internalNodeKey] = InternalNodeSerializer.Instance.Encode(internalNodeValue).Bytes;
    }

    public void RemoveLeaf(ReadOnlySpan<byte> leafKey)
    {
        leafNodeBatch.Remove(leafKey);
    }

    public void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey)
    {
        internalNodeBatch.Remove(internalNodeKey);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        leafNodeBatch.Dispose();
        internalNodeBatch.Dispose();
    }
}

public class VerkleKeyValueDb(IDb internalNodeDb, IDb leafDb) : IVerkleDb, IVerkleKeyValueDb
{
    public VerkleKeyValueDb(IDbProvider dbProvider) : this(dbProvider.InternalNodesDb, dbProvider.LeafDb)
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
        return new VerkleKeyValueBatch(LeafDb.StartWriteBatch(), InternalNodeDb.StartWriteBatch());
    }

    public IDb LeafDb { get; } = leafDb;
    public IDb InternalNodeDb { get; } = internalNodeDb;

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
