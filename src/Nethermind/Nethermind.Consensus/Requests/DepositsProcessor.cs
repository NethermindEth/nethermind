// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Requests;

public class DepositsProcessor : IDepositsProcessor
{
    public List<Deposit>? ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.DepositsEnabled)
            return null;

        List<Deposit> depositList = [];
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            foreach (var log in receipts[i].Logs)
            {
                if (log.LoggersAddress == spec.Eip6110ContractAddress)
                {
                    var depositDecoder = new DepositDecoder();
                    Deposit? deposit = depositDecoder.Decode(new RlpStream(log.Data));
                    depositList.Add(deposit);
                }
            }
        }

        return depositList;
    }
}
