// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Common;

public static class PbtStateKey
{
    /// <summary>The canonical tree leaves of an account, derived from its whole flat value.</summary>
    public static ArrayPoolList<KeyValuePair<PbtPath, ValueHash256>> AccountLeaves(in ValueHash256 addressHash, Account account, CodeInfo? code)
    {
        PbtAccount stem = PbtAccount.From(account, code);
        ArrayPoolList<KeyValuePair<PbtPath, ValueHash256>> leaves = new(2);
        if (stem.BasicData != default) leaves.Add(new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey), stem.BasicData));
        if (stem.IsDelegation)
        {
            leaves.Add(new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.DelegationLeafKey), stem.CodeLeaf));
            return leaves;
        }
        leaves.Add(new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.CodeHashLeafKey), stem.CodeLeaf));
        if (code is null) return leaves;
        foreach (KeyValuePair<PbtPath, ValueHash256> leaf in Eip8297KeyDerivation.CodeLeaves(stem.CodeLeaf, code.Code)) leaves.Add(leaf);
        return leaves;
    }
}
