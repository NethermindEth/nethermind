// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Requests;

public class DepositsProcessor : IDepositsProcessor
{
    public IEnumerable<Deposit> ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (spec.DepositsEnabled)
        {
            DepositDecoder depositDecoder = DepositDecoder.Instance;
            for (int i = 0; i < receipts.Length; i++)
            {
                LogEntry[]? logEntries = receipts[i].Logs;
                if (logEntries is not null)
                {
                    for (int index = 0; index < logEntries.Length; index++)
                    {
                        LogEntry log = logEntries[index];
                        if (log.LoggersAddress == spec.DepositContractAddress)
                        {
                            Deposit? deposit = depositDecoder.Decode(new RlpStream(log.Data));
                            if (deposit is not null)
                            {
                                yield return deposit;
                            }
                        }
                    }
                }
            }
        }
    }
}
