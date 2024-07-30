// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Verkle.Tree.Utils;

public static class AccountExtension
{
    public static LeafInSubTree[] ToVerkleDict(this Account account)
    {
        List<LeafInSubTree> subTree =
        [
            new LeafInSubTree(AccountHeader.BasicDataLeafKey, AccountHeader.AccountToBasicData(account)),
            new LeafInSubTree(AccountHeader.CodeHash, account.CodeHash.Bytes.ToArray())
        ];

        return subTree.ToArray();
    }
}
