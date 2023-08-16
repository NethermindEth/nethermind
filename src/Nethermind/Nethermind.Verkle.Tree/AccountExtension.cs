// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Verkle.Tree;

public static class AccountExtension
{
    public static LeafInSubTree[] ToVerkleDict(this Account account)
    {
        List<LeafInSubTree> subTree = new()
        {
            new LeafInSubTree(0, account.Version.ToLittleEndian()),
            new LeafInSubTree(1, account.Balance.ToLittleEndian()),
            new LeafInSubTree(2, account.Nonce.ToLittleEndian()),
            new LeafInSubTree(3, account.CodeHash.Bytes.ToArray())
        };

        if (!account.CodeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            subTree.Add(new LeafInSubTree(4, account.CodeSize.ToLittleEndian()));
        return subTree.ToArray();
    }
}
