// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using CoreUInt256 = Nethermind.Int256.UInt256;

namespace Nethermind.State.Proofs;

public class WithdrawalSszTrie
{
    public WithdrawalSszTrie(IEnumerable<Withdrawal> withdrawals)
    {
        BuildTree(withdrawals);
    }
    private void BuildTree(IEnumerable<Withdrawal> withdrawals)
    {
        Merkleizer merkleizer = new(Merkle.NextPowerOfTwoExponent(16));

        merkleizer.Feed(withdrawals.ToArray(), 16);
        // foreach (var withdrawal in withdrawals)
        // {
        //     merkleizer.Feed(withdrawal.Index);
        //     merkleizer.Feed(withdrawal.ValidatorIndex);
        //     merkleizer.Feed(withdrawal.Address);
        //     merkleizer.Feed(withdrawal.AmountInGwei);
        // }
        //
        // // Merkle.MixIn(ref root, withdrawals.Count());
        merkleizer.CalculateRoot(out UInt256 root);
        var coreUint = new CoreUInt256(root.S0, root.S1, root.S2, root.S3);
        var hexRoot = coreUint.ToHexString(true);
    }
}
