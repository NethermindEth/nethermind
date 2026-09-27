// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// An EEZL2 <c>CrossChainCallExecuted(bytes32 indexed crossChainCallHash, address indexed proxy, address
/// sourceAddress, bytes callData, uint256 value, uint64 callGas)</c> log. A log with the event's signature that is
/// not its canonical encoding keeps its position without decoded fields, so it can only fail a check later.
/// </summary>
public readonly record struct OutboundEvent(int TransactionIndex, int LogIndex, bool IsCanonical, ValueHash256 CallHash, ulong CallGas)
{
    public static readonly Hash256 Signature = Keccak.Compute(Encoding.ASCII.GetBytes("CrossChainCallExecuted(bytes32,address,address,bytes,uint256,uint64)"));

    private const int Word = AbiWord.Size;
    private const int Head = 4 * Word;

    /// <summary>Every EEZL2 log with the event's signature, in transaction and log order.</summary>
    public static OutboundEvent[] Observe(IReadOnlyList<TxReceipt> receipts)
    {
        List<OutboundEvent> events = [];
        for (int transactionIndex = 0; transactionIndex < receipts.Count; transactionIndex++)
        {
            LogEntry[]? logs = receipts[transactionIndex].Logs;
            for (int logIndex = 0; logs is not null && logIndex < logs.Length; logIndex++)
            {
                LogEntry log = logs[logIndex];
                if (log.Address == EezConstants.Eezl2Address && log.Topics is [{ } topic, ..] && topic == Signature)
                {
                    events.Add(Decode(transactionIndex, logIndex, log));
                }
            }
        }

        return events.ToArray();
    }

    private static OutboundEvent Decode(int transactionIndex, int logIndex, LogEntry log)
    {
        OutboundEvent malformed = new(transactionIndex, logIndex, false, default, 0);
        if (log.Topics is not [_, { } callHash, { } proxy] || !proxy.Bytes[..(Word - Address.Size)].IsZero())
        {
            return malformed;
        }

        try
        {
            AbiReader reader = new(log.Data);
            reader.ReadAddress(0);
            reader.ExpectOffset(Word, 0, Head);
            reader.ReadUInt256(2 * Word);
            ulong callGas = reader.ReadUInt64(3 * Word);
            reader.ReadBytes(Head, out int dataSize);
            return Head + dataSize == log.Data.Length ? new OutboundEvent(transactionIndex, logIndex, true, callHash.ValueHash256, callGas) : malformed;
        }
        catch (EezAbiException)
        {
            return malformed;
        }
    }
}
