// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Checks the L1 entry that executes a call an L2 contract made, against the call event EEZL2 emitted.</summary>
public static class OutboundCall
{
    /// <param name="eventCallHash">The call hash of the EEZL2 <c>CrossChainCallExecuted</c> event.</param>
    /// <param name="eventCallGas">The call gas the event carries; only zero is supported.</param>
    /// <returns>The entry the call's DA sidecar must encode.</returns>
    /// <exception cref="EezSettlementException">The entry does not execute exactly the observed call.</exception>
    public static ExecutionEntry Authorize(ExecutionEntry entry, StateUpdate update, in ValueHash256 eventCallHash, ulong eventCallGas, ulong rollupId)
    {
        Require(entry.Calls.Length == 1, "the entry must carry exactly one call");
        CrossChainCall call = entry.Calls[0];
        Require(entry.DestinationRollupId == rollupId, $"the entry must target rollup {rollupId}");
        Require(call.SourceRollupId == rollupId, $"the call must come from rollup {rollupId}");
        Require(eventCallGas == 0, "only calls without gas are supported");
        ValueHash256 callHash = CrossChainCallHash.Compute(false, call.SourceAddress, rollupId, call.TargetAddress, InboundDelivery.MainnetRollupId,
            call.Value, eventCallGas, call.Data);
        Require(callHash == eventCallHash, "the entry executes a different call than the one emitted");
        ValueHash256 rollingHash = RollingHash.CallEnd(
            RollingHash.CallBegin(RollingHash.SeedL1([new StateCommitment(update.RollupId, update.CurrentState)], entry.ProxyEntryHash), callHash),
            entry.Success, entry.ReturnData);
        Require(entry.RollingHash == rollingHash, "the entry's rolling hash does not record the call");
        Require(call.SourceAddress != EezConstants.SystemAddress, "the system address cannot make outbound calls");
        Require(update.EtherDelta == EtherDelta.Debit(call.Value), "the rollup must be debited the call's value");
        return entry with { StateUpdates = [], RollingHash = default };
    }

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Invalid outbound call: {rule}.");
        }
    }
}
