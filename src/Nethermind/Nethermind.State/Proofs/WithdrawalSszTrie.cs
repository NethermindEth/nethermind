// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Proofs;

public class WithdrawalSszTrie
{
    public void BuildTree(IEnumerable<Withdrawal> withdrawals)
    {
        Merkleizer merkleizer = new();

        foreach (var withdrawal in withdrawals)
        {
            merkleizer.Feed(Rlp.Encode(withdrawal).Bytes);
        }

        merkleizer.CalculateRoot(out UInt256 root);
    }
}
