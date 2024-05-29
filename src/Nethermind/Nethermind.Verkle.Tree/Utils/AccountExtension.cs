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
            new LeafInSubTree(AccountHeader.BasicDataLeafKey, AccountBasicDataToBytes(account)),
            new LeafInSubTree(AccountHeader.CodeHash, account.CodeHash.Bytes.ToArray())
        ];

        return subTree.ToArray();
    }

    public static byte[] AccountBasicDataToBytes(this Account account)
    {
        byte[] basicData = new byte[32];

        byte[] version = account.Version.ToLittleEndian();
        Array.Copy(version, 0, basicData, AccountHeader.VersionOffset, AccountHeader.VersionBytesLength);

        byte[] nonce = account.Nonce.ToLittleEndian();
        Array.Copy(nonce, 0, basicData, AccountHeader.NonceOffset, AccountHeader.NonceBytesLength);

        byte[] codeSize = account.CodeSize.ToLittleEndian();
        Array.Copy(codeSize, 0, basicData, AccountHeader.CodeSizeOffset, AccountHeader.CodeSizeBytesLength);

        byte[] balance = account.Balance.ToLittleEndian();
        Array.Copy(balance, 0, basicData, AccountHeader.BalanceOffset, AccountHeader.BalanceBytesLength);

        return basicData;
    }
}
