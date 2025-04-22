// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Serializers;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.History.V1;

public enum DiffType
{
    Forward,
    Reverse
}

public class DiffLayer
{
    private readonly DiffType _diffType;

    public DiffLayer(IDb diffDb, DiffType diffType)
    {
        DiffDb = diffDb;
        _diffType = diffType;
    }

    private IDb DiffDb { get; }

    public void InsertDiff(long blockNumber, VerkleMemoryDb memory)
    {
        RlpStream stream = new(VerkleMemoryDbSerializer.Instance.GetLength(memory, RlpBehaviors.None));
        VerkleMemoryDbSerializer.Instance.Encode(stream, memory);
        if (stream.Data.Length != 0) DiffDb.Set(blockNumber, stream.Data.ToArray());
    }

    public void InsertDiff(long blockNumber, SortedVerkleMemoryDb memory)
    {
        RlpStream stream = new(VerkleMemoryDbSerializer.Instance.GetLength(memory, RlpBehaviors.None));
        VerkleMemoryDbSerializer.Instance.Encode(stream, memory);
        if (stream.Data.Length != 0) DiffDb.Set(blockNumber, stream.Data.ToArray());
    }

    public VerkleMemoryDb FetchDiff(long blockNumber)
    {
        var diff = DiffDb.Get(blockNumber);
        if (diff is null) throw new ArgumentException(null, nameof(blockNumber));
        return VerkleMemoryDbSerializer.Instance.Decode(diff.AsRlpStream());
    }

    public VerkleMemoryDb MergeDiffs(long fromBlock, long toBlock)
    {
        var mergedDiff = new VerkleMemoryDb();
        switch (_diffType)
        {
            case DiffType.Reverse:
                Debug.Assert(fromBlock > toBlock);
                for (var i = toBlock + 1; i <= fromBlock; i++)
                {
                    VerkleMemoryDb reverseDiff = FetchDiff(i);
                    foreach (KeyValuePair<byte[], byte[]?> item in reverseDiff.LeafTable)
                        mergedDiff.LeafTable.TryAdd(item.Key, item.Value);
                    foreach (KeyValuePair<byte[], InternalNode?> item in reverseDiff.InternalTable)
                        mergedDiff.InternalTable.TryAdd(item.Key, item.Value);
                }

                break;
            case DiffType.Forward:
                Debug.Assert(fromBlock < toBlock);
                for (var i = toBlock; i >= fromBlock; i--)
                {
                    VerkleMemoryDb forwardDiff = FetchDiff(i);
                    foreach (KeyValuePair<byte[], byte[]?> item in forwardDiff.LeafTable)
                        mergedDiff.LeafTable.TryAdd(item.Key, item.Value);
                    foreach (KeyValuePair<byte[], InternalNode?> item in forwardDiff.InternalTable)
                        mergedDiff.InternalTable.TryAdd(item.Key, item.Value);
                }

                break;
            default:
                throw new NotSupportedException();
        }

        return mergedDiff;
    }

    public byte[]? GetLeaf(long blockNumber, ReadOnlySpan<byte> key)
    {
        VerkleMemoryDb diff = FetchDiff(blockNumber);
        diff.GetLeaf(key, out var value);
        return value;
    }
}
