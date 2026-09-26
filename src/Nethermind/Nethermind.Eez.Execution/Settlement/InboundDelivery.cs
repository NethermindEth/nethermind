// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Checks an inbound delivery: the L2 system transaction's <c>executeIncomingCrossChainCall</c> and the L1 entry
/// that claims its result.
/// </summary>
public static class InboundDelivery
{
    public const ulong MainnetRollupId = 0;

    /// <exception cref="EezSettlementException">The delivery is not a single, flat, successful L1-to-L2 call.</exception>
    public static InboundObservation Inspect(in UInt256 transactionValue, ReadOnlySpan<byte> calldata, bool transactionSucceeded, ulong rollupId)
    {
        Require(transactionSucceeded, "the delivery reverted");
        IncomingCrossChainCall call;
        try
        {
            call = EezCalldata.DecodeExecuteIncomingCrossChainCall(calldata);
        }
        catch (EezAbiException e)
        {
            throw new EezSettlementException($"Invalid inbound delivery: {e.Message}");
        }

        Require(transactionValue == call.Value, "the native value differs from the delivered value");
        Require(call.SourceRollup == MainnetRollupId, "the call does not come from L1");
        Require(call.Entries.Length == 1, "the delivery must load exactly one entry");
        Require(call.StaticEntries.Length == 0, "the delivery must load no static entries");
        L2ExecutionEntry entry = call.Entries[0];
        Require(entry.IncomingCalls.Length == 1, "the entry must carry exactly one incoming call");
        Require(entry.Success, "the entry must succeed");
        Require(entry.ExpectedOutgoingCalls.Length == 0, "the entry must expect no outgoing calls");
        CrossChainCall inner = entry.IncomingCalls[0];
        Require(call.Destination == inner.TargetAddress && call.Value == inner.Value && call.Data.AsSpan().SequenceEqual(inner.Data)
            && call.SourceAddress == inner.SourceAddress && call.SourceRollup == inner.SourceRollupId,
            "the delivered call differs from the call in its entry");
        Require(inner is { RevertNextNCalls: 0, IsStatic: false, Gas: 0 }, "the incoming call must be flat, mutable and without gas");

        ValueHash256 callHash = CrossChainCallHash.Compute(false, call.SourceAddress, MainnetRollupId, call.Destination, rollupId, call.Value, 0, call.Data);
        Require(callHash != default, "the call hash is zero");
        Require(entry.ProxyEntryHash == callHash, "the entry's proxy entry hash is not the delivered call's hash");
        ValueHash256 rollingHash = RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.SeedL2(callHash), callHash), entry.Success, entry.ReturnData);
        Require(entry.RollingHash == rollingHash, "the entry's rolling hash does not record the delivered call");

        ExecutionEntry derived = new([], callHash, [inner], [], entry.RollingHash, rollupId, entry.Success, entry.ReturnData);
        return new InboundObservation(callHash, call.Value, entry.ReturnData, derived);
    }

    /// <exception cref="EezSettlementException">The L1 entry does not claim exactly the observed delivery.</exception>
    public static void Authorize(ExecutionEntry entry, StateUpdate update, InboundObservation observation, ulong rollupId)
    {
        Require(entry.DestinationRollupId == rollupId, $"the entry must target rollup {rollupId}");
        Require(entry.ProxyEntryHash == observation.CallHash, "the entry claims a different call");
        Require(entry.ReturnData.AsSpan().SequenceEqual(observation.ReturnData), "the entry claims a different result");
        Require(entry.RollingHash == RollingHash.SeedL1([new StateCommitment(update.RollupId, update.CurrentState)], entry.ProxyEntryHash),
            "the entry's rolling hash is not its L1 seed");
        Require(update.EtherDelta == EtherDelta.Credit(observation.Value), "the rollup must be credited the delivered value");
    }

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Invalid inbound delivery: {rule}.");
        }
    }
}
