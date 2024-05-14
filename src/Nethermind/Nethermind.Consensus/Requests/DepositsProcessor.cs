// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using System.Linq;
using System;

namespace Nethermind.Consensus.Requests;

public class DepositsProcessor : IDepositsProcessor
{
    private AbiSignature depositEventABI = new AbiSignature("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    AbiEncoder abiEncoder = new AbiEncoder();

    public IEnumerable<Deposit> ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (spec.DepositsEnabled)
        {
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
                            var result = abiEncoder.Decode(AbiEncodingStyle.None, depositEventABI, log.Data);

                            var newDeposit = new Deposit()
                            {
                                PubKey = (byte[])result[0],
                                WithdrawalCredentials = (byte[])result[1],
                                Amount = BitConverter.ToUInt64(((byte[])result[2]).Reverse().ToArray(), 0),
                                Signature = (byte[])result[3],
                                Index = BitConverter.ToUInt64(((byte[])result[4]).Reverse().ToArray(), 0)
                            };
                            yield return newDeposit;
                        }
                    }
                }
            }
        }
    }
}
