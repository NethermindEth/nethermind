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
    private readonly AbiSignature _depositEventAbi = new("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);
    private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

    public IEnumerable<Deposit> ProcessDeposits(Block block, TxReceipt[] receipts, IReleaseSpec spec)
    {
        if (!spec.DepositsEnabled)
        {
            yield break;
        }

        for (int i = 0; i < receipts.Length; i++)
        {
            LogEntry[]? logEntries = receipts[i].Logs;
            if (logEntries is not null)
            {
                for (var j = 0; j < logEntries.Length; j++)
                {
                    LogEntry log = logEntries[j];
                    if (log.LoggersAddress == spec.DepositContractAddress)
                    {
                        yield return DecodeDeposit(log);
                    }
                }
            }
        }

        Deposit DecodeDeposit(LogEntry log)
        {
            object[] result = _abiEncoder.Decode(AbiEncodingStyle.None, _depositEventAbi, log.Data);

            return new Deposit
            {
                Pubkey = (byte[])result[0],
                WithdrawalCredentials = (byte[])result[1],
                Amount = BitConverter.ToUInt64((byte[])result[2], 0),
                Signature = (byte[])result[3],
                Index = BitConverter.ToUInt64((byte[])result[4], 0)
            };
        }
    }
}
