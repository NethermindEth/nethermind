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
    public LeafStore.AlternateLookup<ReadOnlySpan<byte>> LeafTableSpan = leafTable.GetAlternateLookup<ReadOnlySpan<byte>>();
    public InternalStore InternalTable { get; } = internalTable;
    public InternalStore.AlternateLookup<ReadOnlySpan<byte>> InternalTableSpan = internalTable.GetAlternateLookup<ReadOnlySpan<byte>>();

    public VerkleMemoryDb() : this(new LeafStore(Bytes.EqualityComparer), new InternalStore(Bytes.EqualityComparer))
    {
    }

    public bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value)
    {
        return LeafTableSpan.TryGetValue(key, out value);
    }

    public bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode value)
    {
        return InternalTableSpan.TryGetValue(key, out value);
    }

    public bool HasLeaf(ReadOnlySpan<byte> key)
    {
        return LeafTableSpan.ContainsKey(key);
    }

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        LeafTableSpan[leafKey] = leafValue;
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        InternalTableSpan[internalNodeKey] = internalNodeValue;
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
