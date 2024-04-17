// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Withdrawals;

public class DepositsProcessor : IDepositsProcessor
{
    private readonly ILogger _logger;

    public DepositsProcessor(ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
    }

    public void ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.DepositsEnabled)
            return;


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

        CalculateDepositsRoot(block, depositList, spec);
    }

    private void CalculateDepositsRoot(Block block, IEnumerable<Deposit> deposits, IReleaseSpec spec)
    {
        block.Header.DepositsRoot = deposits.IsNullOrEmpty()
            ? Keccak.EmptyTreeHash
            : new DepositTrie(deposits.ToArray()!).RootHash;
    }
}
