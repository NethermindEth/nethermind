// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class SortedVerkleMemoryDb(InternalStore internalTable, LeafStore leafTable)
{
    public readonly InternalStore InternalTable = internalTable;
    public readonly LeafStoreSorted LeafTable = new(leafTable, Bytes.Comparer);
}

public class VerkleMemoryDb(LeafStore leafTable, InternalStore internalTable) : IVerkleDb, IVerkleMemDb
{
    public LeafStore LeafTable { get; } = leafTable;
    public InternalStore InternalTable { get; } = internalTable;

    public VerkleMemoryDb() : this(new LeafStore(Bytes.SpanEqualityComparer), new InternalStore(Bytes.SpanEqualityComparer))
    {
    }

    public bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value)
    {
        return LeafTable.TryGetValue(key, out value);
    }

    public bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode? value)
    {
        return InternalTable.TryGetValue(key, out value);
    }

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        LeafTable[leafKey] = leafValue;
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        InternalTable[internalNodeKey] = internalNodeValue;
    }

    public void RemoveLeaf(ReadOnlySpan<byte> leafKey)
    {
        LeafTable.Remove(leafKey.ToArray(), out _);
    }

    public void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey)
    {
        InternalTable.Remove(internalNodeKey.ToArray(), out _);
    }

    public SortedVerkleMemoryDb ToSortedVerkleDb() => new(InternalTable, LeafTable);
}
