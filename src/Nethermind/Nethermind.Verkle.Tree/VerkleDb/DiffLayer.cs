// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Serializers;

namespace Nethermind.Verkle.Tree.VerkleDb;

public enum DiffType
{
    Forward,
    Reverse
}


public class DiffLayer
{
    private readonly DiffType _diffType;
    private IDb DiffDb { get; }
    public DiffLayer(IDb diffDb, DiffType diffType)
    {
        DiffDb = diffDb;
        _diffType = diffType;
    }
    public void InsertDiff(long blockNumber, VerkleMemoryDb memory)
    {
        RlpStream stream = new(VerkleMemoryDbSerializer.Instance.GetLength(memory, RlpBehaviors.None));
        VerkleMemoryDbSerializer.Instance.Encode(stream, memory);
        if (stream.Data != null) DiffDb.Set(blockNumber, stream.Data);
    }

    public void InsertDiff(long blockNumber, ReadOnlyVerkleMemoryDb memory)
    {
        RlpStream stream = new(VerkleMemoryDbSerializer.Instance.GetLength(memory, RlpBehaviors.None));
        VerkleMemoryDbSerializer.Instance.Encode(stream, memory);
        if (stream.Data != null) DiffDb.Set(blockNumber, stream.Data);
    }

    public VerkleMemoryDb FetchDiff(long blockNumber)
    {
        byte[]? diff = DiffDb.Get(blockNumber);
        if (diff is null) throw new ArgumentException(null, nameof(blockNumber));
        return VerkleMemoryDbSerializer.Instance.Decode(diff.AsRlpStream());
    }

    public VerkleMemoryDb MergeDiffs(long fromBlock, long toBlock)
    {
        VerkleMemoryDb mergedDiff = new VerkleMemoryDb();
        switch (_diffType)
        {
            case DiffType.Reverse:
                Debug.Assert(fromBlock > toBlock);
                for (long i = toBlock + 1; i <= fromBlock; i++)
                {
                    VerkleMemoryDb reverseDiff = FetchDiff(i);
                    foreach (KeyValuePair<byte[], byte[]?> item in reverseDiff.LeafTable)
                    {
                        mergedDiff.LeafTable.TryAdd(item.Key, item.Value);
                    }
                    foreach (KeyValuePair<byte[], InternalNode?> item in reverseDiff.InternalTable)
                    {
                        mergedDiff.InternalTable.TryAdd(item.Key, item.Value);
                    }
                }
                break;
            case DiffType.Forward:
                Debug.Assert(fromBlock < toBlock);
                for (long i = toBlock; i >= fromBlock; i--)
                {
                    VerkleMemoryDb forwardDiff = FetchDiff(i);
                    foreach (KeyValuePair<byte[], byte[]?> item in forwardDiff.LeafTable)
                    {
                        mergedDiff.LeafTable.TryAdd(item.Key, item.Value);
                    }
                    foreach (KeyValuePair<byte[], InternalNode?> item in forwardDiff.InternalTable)
                    {
                        mergedDiff.InternalTable.TryAdd(item.Key, item.Value);
                    }
                }
                break;
            default:
                throw new NotSupportedException();
        }
        return mergedDiff;
    }
}
