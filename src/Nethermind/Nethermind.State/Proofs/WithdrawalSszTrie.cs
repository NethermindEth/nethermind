// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using Nethermind.Serialization.Rlp;
using CoreUInt256 = Nethermind.Int256.UInt256;

namespace Nethermind.State.Proofs;

public class WithdrawalSszTrie
{
    public Keccak Root { get; private set; }
    public WithdrawalSszTrie(IEnumerable<Withdrawal> withdrawals)
    {
        BuildTree(withdrawals);
    }
    private void BuildTree(IEnumerable<Withdrawal> withdrawals)
    {

        foreach (var withdrawal in withdrawals)
        {
            Span<byte> span = new byte[Nethermind.Ssz.Ssz.WithdrawalDataLength];
            Ssz.Ssz.Encode(span, withdrawal);
        }

        Merkleizer merkleizer = new(Merkle.NextPowerOfTwoExponent(4));
        foreach (var withdrawal in withdrawals)
        {
            merkleizer.Feed(withdrawal.Index);
            merkleizer.Feed(withdrawal.ValidatorIndex);
            merkleizer.Feed(withdrawal.Address);
            merkleizer.Feed(withdrawal.AmountInGwei);
            var bytes = Rlp.Encode(withdrawal).Bytes;
            //   Ssz.Ssz.Encode(span, withdrawal);
            //  merkleizer.Feed(withdrawal);
        }



     //   merkleizer.Feed(withdrawals.ToArray(), 16);
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
        var reverseRoot = new Keccak(coreUint.ToHexString(true));
        Root = new Keccak(reverseRoot.Bytes.ToArray().Reverse().ToArray().ToHexString());
    }
}
