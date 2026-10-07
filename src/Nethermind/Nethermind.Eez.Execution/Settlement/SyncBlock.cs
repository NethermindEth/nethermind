// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The canonical transactions of a Sync block, rebuilt from its effect entries: for each outbound call a
/// <c>loadExecutionTable</c> system transaction followed by the user transaction that consumes it, then one
/// <c>executeIncomingCrossChainCall</c> system transaction per inbound call. Each load replaces the execution
/// table and resets its cursor, so every user transaction runs before the next load. System nonces run in
/// transaction order from the system account's nonce at the parent.
/// </summary>
public static class SyncBlock
{
    /// <param name="outbound">Each outbound entry, as its DA sidecar carries it, and the user transaction that consumes it.</param>
    /// <param name="inbound">The inbound entries, as their DA sidecars carry them.</param>
    /// <exception cref="EezSettlementException">An entry cannot be lowered, or the nonces overflow.</exception>
    public static byte[][] BuildTransactions(IReadOnlyList<(ExecutionEntry Entry, byte[] UserTransaction)> outbound, IReadOnlyList<ExecutionEntry> inbound,
        ulong chainId, ulong rollupId, ulong startingNonce)
    {
        foreach (ExecutionEntry entry in inbound)
        {
            EnsureLowerable(entry, "inbound");
            if (entry.DestinationRollupId != rollupId || entry.Calls.Length != 1)
            {
                throw new EezSettlementException($"An inbound entry must deliver exactly one call to rollup {rollupId}.");
            }
        }

        List<byte[]> transactions = new(2 * outbound.Count + inbound.Count);
        ulong nonce = startingNonce;
        foreach ((ExecutionEntry entry, byte[] userTransaction) in outbound)
        {
            EnsureLowerable(entry, "outbound");
            if (entry.Calls is not [{ } call])
            {
                throw new EezSettlementException("An outbound entry must carry exactly one call.");
            }

            ValueHash256 proxy = CrossChainCallHash.Compute(false, call.SourceAddress, rollupId, call.TargetAddress, EezConstants.L1RollupId,
                call.Value, 0, call.Data);
            L2ExecutionEntry table = new(proxy, [], [], RollingHash.SeedL2(proxy), true, entry.ReturnData);
            transactions.Add(Encode(chainId, NextNonce(ref nonce), UInt256.Zero, EezCalldata.EncodeLoadExecutionTable(new ExecutionTable([table], []))));
            transactions.Add(userTransaction);
        }

        foreach (ExecutionEntry entry in inbound)
        {
            CrossChainCall outer = entry.Calls[0];
            ValueHash256 callHash = CrossChainCallHash.Compute(false, outer.SourceAddress, outer.SourceRollupId, outer.TargetAddress, rollupId,
                outer.Value, 0, outer.Data);
            ValueHash256 rollingHash = RollingHash.SingleL2Call(callHash, true, entry.ReturnData);
            CrossChainCall incoming = new(0, false, 0, outer.SourceAddress, outer.SourceRollupId, outer.TargetAddress, outer.Value, outer.Data);
            L2ExecutionEntry delivery = new(callHash, [incoming], [], rollingHash, true, entry.ReturnData);
            IncomingCrossChainCall call = new([delivery], []);
            transactions.Add(Encode(chainId, NextNonce(ref nonce), outer.Value, EezCalldata.EncodeExecuteIncomingCrossChainCall(call)));
        }

        return transactions.ToArray();
    }

    /// <summary>The unsigned native system transaction every role reconstructs identically.</summary>
    public static byte[] Encode(ulong chainId, ulong nonce, in UInt256 value, byte[] calldata)
    {
        Transaction transaction = new()
        {
            Type = EezConstants.SystemTxType,
            ChainId = chainId,
            Nonce = nonce,
            To = EezConstants.Eezl2Address,
            Value = value,
            Data = calldata,
        };
        return TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
    }

    private static ulong NextNonce(ref ulong nonce) =>
        nonce == ulong.MaxValue ? throw new EezSettlementException("The system account nonce overflows.") : nonce++;

    private static void EnsureLowerable(ExecutionEntry entry, string direction)
    {
        if (entry.Calls.Length > 1)
        {
            throw new EezSettlementException($"An {direction} entry with {entry.Calls.Length} calls is not supported.");
        }

        if (entry.ExpectedCalls.Length != 0 || !entry.Success)
        {
            throw new EezSettlementException($"An {direction} entry must succeed and expect no calls.");
        }

        if (entry.Calls is [{ IsStatic: true } or { RevertNextNCalls: > 0 } or { Gas: > 0 }])
        {
            throw new EezSettlementException($"An {direction} entry must carry a flat, mutable call without gas.");
        }
    }
}
