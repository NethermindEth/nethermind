// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// Checks an inbound delivery: the L2 system transaction's <c>executeIncomingCrossChainCall</c> and the L1 entry
/// that claims its result.
/// </summary>
public static class InboundDelivery
{
    /// <summary>
    /// Inspects a delivery: its entry's single incoming call is the delivered call, so the call's hash, value and
    /// source are read from it.
    /// </summary>
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

        Require(call.Entries.Length == 1, "the delivery must load exactly one entry");
        Require(call.StaticEntries.Length == 0, "the delivery must load no static entries");
        L2ExecutionEntry entry = call.Entries[0];
        Require(entry.IncomingCalls.Length == 1, "the entry must carry exactly one incoming call");
        Require(entry.Success, "the entry must succeed");
        Require(entry.ExpectedOutgoingCalls.Length == 0, "the entry must expect no outgoing calls");
        CrossChainCall delivered = entry.IncomingCalls[0];
        Require(delivered is { RevertNextNCalls: 0, IsStatic: false, Gas: 0 }, "the incoming call must be flat, mutable and without gas");
        Require(delivered.SourceRollupId == EezConstants.L1RollupId, "the call does not come from L1");
        Require(transactionValue == delivered.Value, "the native value differs from the delivered value");

        ValueHash256 callHash = CrossChainCallHash.Compute(false, delivered.SourceAddress, EezConstants.L1RollupId, delivered.TargetAddress, rollupId,
            delivered.Value, 0, delivered.Data);
        Require(callHash != default, "the call hash is zero");
        Require(entry.ProxyEntryHash == callHash, "the entry's proxy entry hash is not the delivered call's hash");
        ValueHash256 rollingHash = RollingHash.SingleL2Call(callHash, entry.Success, entry.ReturnData);
        Require(entry.RollingHash == rollingHash, "the entry's rolling hash does not record the delivered call");

        ExecutionEntry derived = new([], callHash, [delivered], [], entry.RollingHash, rollupId, entry.Success, entry.ReturnData);
        return new InboundObservation(callHash, delivered.Value, entry.ReturnData, derived);
    }

    /// <summary>
    /// Binds every inbound effect to the delivery at its transaction, and every delivery to an inbound effect.
    /// </summary>
    /// <param name="effects">The bound effects, ascending by transaction.</param>
    /// <param name="candidates">The settling block's deliveries, ascending by transaction.</param>
    /// <exception cref="EezSettlementException">A delivery is missing, unclaimed, invalid or claimed differently.</exception>
    public static AuthorizedInbound[] AuthorizeAll(BoundEffect[] effects, InboundCandidate[] candidates, ulong rollupId)
    {
        List<AuthorizedInbound> authorized = new(candidates.Length);
        int next = 0;
        foreach (BoundEffect effect in effects)
        {
            if (effect.Shape != EntryShape.Inbound)
            {
                continue;
            }

            if (next == candidates.Length || candidates[next].TransactionIndex != effect.TransactionIndex)
            {
                throw new EezSettlementException($"Inbound entry {effect.EntryIndex} has no delivery at transaction {effect.TransactionIndex}.");
            }

            InboundCandidate candidate = candidates[next++];
            InboundObservation observation = candidate.Observation
                ?? throw new EezSettlementException($"Inbound entry {effect.EntryIndex} claims an invalid delivery: {candidate.Error}")
                {
                    PoisonedEntryIndex = candidate.Reverted ? effect.EntryIndex : null,
                };
            Authorize(effect.Entry, effect.Update, observation, rollupId);
            authorized.Add(new AuthorizedInbound(effect.TransactionIndex, observation));
        }

        return next < candidates.Length ? throw Unclaimed(candidates[next]) : authorized.ToArray();
    }

    /// <exception cref="EezSettlementException">The L1 entry does not claim exactly the observed delivery.</exception>
    public static void Authorize(ExecutionEntry entry, RollupUpdate update, InboundObservation observation, ulong rollupId)
    {
        Require(entry.DestinationRollupId == rollupId, $"the entry must target rollup {rollupId}");
        Require(entry.ProxyEntryHash == observation.CallHash, "the entry claims a different call");
        Require(entry.ReturnData.AsSpan().SequenceEqual(observation.ReturnData), "the entry claims a different result");
        Require(entry.RollingHash == RollingHash.SeedL1(update, entry.ProxyEntryHash),
            "the entry's rolling hash is not its L1 seed");
        Require(update.EtherDelta == EtherDelta.Credit(observation.Value), "the rollup must be credited the delivered value");
    }

    private static EezSettlementException Unclaimed(InboundCandidate candidate) =>
        new($"The delivery at transaction {candidate.TransactionIndex} is claimed by no inbound entry.");

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new EezSettlementException($"Invalid inbound delivery: {rule}.");
        }
    }
}
