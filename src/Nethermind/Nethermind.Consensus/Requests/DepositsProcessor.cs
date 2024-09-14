// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Requests;

public class DepositsProcessor : IDepositsProcessor
{
    private AbiSignature depositEventABI = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    AbiEncoder abiEncoder = new();

    public IEnumerable<Deposit> ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.DepositsEnabled)
            yield break;
        for (int i = 0; i < receipts.Length; i++)
        {
            LogEntry[]? logEntries = receipts[i].Logs;
            if (logEntries is null)
                continue;
            for (int index = 0; index < logEntries.Length; index++)
            {
                LogEntry log = logEntries[index];
                if (log.LoggersAddress != spec.DepositContractAddress)
                    continue;
                var result = (byte[][])abiEncoder.Decode(AbiEncodingStyle.None, depositEventABI, log.Data);

                var newDeposit = new Deposit()
                {
                    Pubkey = result[0],
                    WithdrawalCredentials = result[1],
                    Amount = BitConverter.ToUInt64(result[2], 0),
                    Signature = result[3],
                    Index = BitConverter.ToUInt64(result[4], 0)
                };

                yield return newDeposit;
            }
        }
    }
}