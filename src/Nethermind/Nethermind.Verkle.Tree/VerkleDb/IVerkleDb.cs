// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public interface IVerkleDb
{
    bool GetLeaf(byte[] key, out byte[]? value);
    bool GetStem(byte[] key, out SuffixTree? value);
    bool GetBranch(byte[] key, out InternalNode? value);

    void SetLeaf(byte[] leafKey, byte[] leafValue);
    void SetStem(byte[] stemKey, SuffixTree suffixTree);
    void SetBranch(byte[] branchKey, InternalNode internalNodeValue);

    void RemoveLeaf(byte[] leafKey);
    void RemoveStem(byte[] stemKey);
    void RemoveBranch(byte[] branchKey);

    void BatchLeafInsert(IEnumerable<KeyValuePair<byte[], byte[]?>> keyLeaf);
    void BatchStemInsert(IEnumerable<KeyValuePair<byte[], SuffixTree?>> suffixLeaf);
    void BatchBranchInsert(IEnumerable<KeyValuePair<byte[], InternalNode?>> branchLeaf);
}
