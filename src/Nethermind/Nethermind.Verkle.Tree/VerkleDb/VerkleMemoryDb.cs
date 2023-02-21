// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleMemoryDb: IVerkleDb, IVerkleMemDb
{
    public LeafStore LeafTable { get; }
    public StemStore StemTable { get; }
    public BranchStore BranchTable { get; }

    public VerkleMemoryDb()
    {
        LeafTable = new LeafStore(Bytes.EqualityComparer);
        StemTable = new StemStore(Bytes.EqualityComparer);
        BranchTable = new BranchStore(Bytes.EqualityComparer);
    }

    public VerkleMemoryDb(LeafStore leafTable, StemStore stemTable, BranchStore branchTable)
    {
        LeafTable = leafTable;
        StemTable = stemTable;
        BranchTable = branchTable;
    }

    public bool GetLeaf(byte[] key, out byte[]? value) => LeafTable.TryGetValue(key, out value);
    public bool GetStem(byte[] key, out SuffixTree? value) => StemTable.TryGetValue(key, out value);
    public bool GetBranch(byte[] key, out InternalNode? value) => BranchTable.TryGetValue(key, out value);

    public void SetLeaf(byte[] leafKey, byte[] leafValue) => LeafTable[leafKey] = leafValue;
    public void SetStem(byte[] stemKey, SuffixTree suffixTree) => StemTable[stemKey] = suffixTree;
    public void SetBranch(byte[] branchKey, InternalNode internalNodeValue) => BranchTable[branchKey] = internalNodeValue;

    public void RemoveLeaf(byte[] leafKey) => LeafTable.Remove(leafKey, out _);
    public void RemoveStem(byte[] stemKey) =>   StemTable.Remove(stemKey, out _);
    public void RemoveBranch(byte[] branchKey) => BranchTable.Remove(branchKey, out _);

    public void BatchLeafInsert(IEnumerable<KeyValuePair<byte[], byte[]?>> keyLeaf)
    {
        foreach ((byte[] key, byte[]? value) in keyLeaf)
        {
            SetLeaf(key, value);
        }
    }
    public void BatchStemInsert(IEnumerable<KeyValuePair<byte[], SuffixTree?>> suffixLeaf)
    {
        foreach ((byte[] key, SuffixTree? value) in suffixLeaf)
        {
            SetStem(key, value);
        }
    }
    public void BatchBranchInsert(IEnumerable<KeyValuePair<byte[], InternalNode?>> branchLeaf)
    {
        foreach ((byte[] key, InternalNode? value) in branchLeaf)
        {
            SetBranch(key, value);
        }
    }


}
