// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>Checks the L1 entry that executes a call an L2 contract made, against the call event EEZL2 emitted.</summary>
public static class OutboundCall
{
    /// <summary>
    /// Binds every outbound effect to the one event its transaction emitted and to the system load right before
    /// it, and every event to an outbound effect. Outbound effects precede inbound ones.
    /// </summary>
    /// <param name="effects">The bound effects, ascending by transaction.</param>
    /// <param name="events">The settling block's outbound events, in transaction and log order.</param>
    /// <param name="systemTransactions">Whether each transaction of the settling block is a system transaction.</param>
    /// <exception cref="EezSettlementException">An event is missing, unclaimed, malformed or claimed differently.</exception>
    public static AuthorizedOutbound[] AuthorizeAll(BoundEffect[] effects, OutboundEvent[] events, ReadOnlySpan<bool> systemTransactions, ulong rollupId)
    {
        List<AuthorizedOutbound> authorized = new(events.Length);
        int next = 0;
        bool sawInbound = false;
        foreach (BoundEffect effect in effects)
        {
            if (effect.Shape == EntryShape.Inbound)
            {
                sawInbound = true;
                continue;
            }

            if (sawInbound)
            {
                throw new EezSettlementException($"Outbound entry {effect.EntryIndex} follows an inbound entry.");
            }

            int load = effect.TransactionIndex - 1;
            if (load < 0 || !systemTransactions[load])
            {
                throw new EezSettlementException($"Outbound entry {effect.EntryIndex} at transaction {effect.TransactionIndex} has no system load before it.");
            }

            if (next == events.Length || events[next].TransactionIndex != effect.TransactionIndex)
            {
                throw new EezSettlementException($"Outbound entry {effect.EntryIndex} has no event at transaction {effect.TransactionIndex}.")
                {
                    PoisonedTransactionIndex = effect.TransactionIndex,
                };
            }

            OutboundEvent observed = events[next++];
            if (!observed.IsCanonical || observed.CallGas != 0)
            {
                throw new EezSettlementException(
                    $"The outbound event at transaction {observed.TransactionIndex} log {observed.LogIndex} is {(observed.IsCanonical ? "a call with gas, which is not supported" : "malformed")}.")
                {
                    PoisonedTransactionIndex = observed.TransactionIndex,
                };
            }

            (ExecutionEntry baseDaEntry, ValueHash256 pending) = Authorize(effect.Entry, effect.Update, observed.CallHash, observed.CallGas, rollupId);
            authorized.Add(new AuthorizedOutbound(load, effect.TransactionIndex, baseDaEntry, pending, effect.Entry.RollingHash));
        }

        return next < events.Length ? throw Unclaimed(events[next]) : authorized.ToArray();
    }

    /// <param name="eventCallHash">The call hash of the EEZL2 <c>CrossChainCallExecuted</c> event.</param>
    /// <param name="eventCallGas">The call gas the event carries; only zero is supported.</param>
    /// <returns>
    /// The entry the call's DA sidecar carries before its result, and the rolling hash up to the call's begin. An
    /// immediate entry publishes no result on L1, so the result and the rolling hash's end are checked against DA.
    /// </returns>
    /// <exception cref="EezSettlementException">The entry does not execute exactly the observed call.</exception>
    public static (ExecutionEntry BaseDaEntry, ValueHash256 PendingRollingHash) Authorize(ExecutionEntry entry, RollupUpdate update, in ValueHash256 eventCallHash,
        ulong eventCallGas, ulong rollupId)
    {
        Require(entry.Calls.Length == 1, "the entry must carry exactly one call");
        CrossChainCall call = entry.Calls[0];
        Require(entry.DestinationRollupId == rollupId, $"the entry must target rollup {rollupId}");
        Require(call.SourceRollupId == rollupId, $"the call must come from rollup {rollupId}");
        Require(entry.Success && entry.ReturnData.Length == 0, "an immediate entry must succeed and carry no return data");
        Require(eventCallGas == 0, "only calls without gas are supported");
        ValueHash256 callHash = CrossChainCallHash.Compute(false, call.SourceAddress, rollupId, call.TargetAddress, EezConstants.L1RollupId,
            call.Value, eventCallGas, call.Data);
        Require(callHash == eventCallHash, "the entry executes a different call than the one emitted");
        Require(call.SourceAddress != EezConstants.SystemAddress, "the system address cannot make outbound calls");
        Require(update.EtherDelta == EtherDelta.Debit(call.Value), "the rollup must be debited the call's value");
        ValueHash256 pending = RollingHash.CallBegin(RollingHash.SeedL1(update, entry.ProxyEntryHash), callHash);
        return (entry with { RollupUpdates = [], RollingHash = default }, pending);
    }

    private static EezSettlementException Unclaimed(OutboundEvent observed) =>
        new($"The outbound event at transaction {observed.TransactionIndex} log {observed.LogIndex} is claimed by no outbound entry.");

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Invalid outbound call: {rule}.");
        }
    }
}
