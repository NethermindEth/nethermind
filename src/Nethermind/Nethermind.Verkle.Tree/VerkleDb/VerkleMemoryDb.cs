// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class ReadOnlyVerkleMemoryDb
{
    public LeafStoreSorted LeafTable;
    public InternalStore InternalTable;
}

public class VerkleMemoryDb : IVerkleDb, IVerkleMemDb
{
    public LeafStore LeafTable { get; }
    public InternalStore InternalTable { get; }

    public VerkleMemoryDb()
    {
        LeafTable = new LeafStore(Bytes.SpanEqualityComparer);
        InternalTable = new InternalStore(Bytes.SpanEqualityComparer);
    }

    public VerkleMemoryDb(LeafStore leafTable, InternalStore internalTable)
    {
        LeafTable = leafTable;
        InternalTable = internalTable;
    }

    public bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value) => LeafTable.TryGetValue(key, out value);
    public bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode? value) => InternalTable.TryGetValue(key, out value);

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue) => LeafTable[leafKey] = leafValue;
    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue) => InternalTable[internalNodeKey] = internalNodeValue;

    public void RemoveLeaf(ReadOnlySpan<byte> leafKey) => LeafTable.Remove(leafKey.ToArray(), out _);
    public void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey) => InternalTable.Remove(internalNodeKey.ToArray(), out _);

    public void BatchLeafInsert(IEnumerable<KeyValuePair<byte[], byte[]?>> keyLeaf)
    {
        foreach ((byte[] key, byte[]? value) in keyLeaf)
        {
            SetLeaf(key, value);
        }
    }

    public void BatchInternalNodeInsert(IEnumerable<KeyValuePair<byte[], InternalNode?>> internalNodeKey)
    {
        foreach ((byte[] key, InternalNode? value) in internalNodeKey)
        {
            SetInternalNode(key, value);
        }
    }


}
