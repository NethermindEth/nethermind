// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// One cross-chain call as the DA stream publishes it: an <c>Initiate</c>, <c>Call</c>, <c>Return</c> and
/// <c>Finish</c> bracket.
/// </summary>
public sealed record DaAction(
    ulong SourceRollupId,
    ulong TargetRollupId,
    Address SourceAddress,
    Address TargetAddress,
    UInt256 Value,
    ulong Gas,
    byte[] Data,
    bool Success,
    byte[] ReturnData)
{
    /// <summary>The entry the action stands for, rebuilt the way derivation rebuilds it.</summary>
    /// <exception cref="EezSettlementException">The action neither leaves nor enters <paramref name="rollupId"/>.</exception>
    public ExecutionEntry ToEntry(ulong rollupId)
    {
        bool outbound = SourceRollupId == rollupId && TargetRollupId == InboundDelivery.MainnetRollupId;
        bool inbound = TargetRollupId == rollupId;
        if (!outbound && !inbound)
        {
            throw new EezSettlementException($"DA action from rollup {SourceRollupId} to rollup {TargetRollupId} does not involve rollup {rollupId}.");
        }

        CrossChainCall call = new(0, false, Gas, SourceAddress, SourceRollupId, TargetAddress, Value, Data);
        if (outbound)
        {
            return new ExecutionEntry([], default, [call], [], default, rollupId, Success, ReturnData);
        }

        ValueHash256 callHash = CrossChainCallHash.Compute(false, SourceAddress, SourceRollupId, TargetAddress, TargetRollupId, Value, 0, Data);
        ValueHash256 rollingHash = RollingHash.CallEnd(RollingHash.CallBegin(RollingHash.SeedL2(callHash), callHash), Success, ReturnData);
        return new ExecutionEntry([], callHash, [call], [], rollingHash, rollupId, Success, ReturnData);
    }

    /// <summary>The action that publishes <paramref name="entry"/>, the inverse of <see cref="ToEntry"/>.</summary>
    /// <exception cref="EezSettlementException">The entry is not a single flat call that leaves or enters <paramref name="rollupId"/>.</exception>
    public static DaAction FromEntry(ExecutionEntry entry, ulong rollupId)
    {
        if (entry.Calls is not [{ } call])
        {
            throw new EezSettlementException("A DA action needs exactly one call on its entry.");
        }

        if (entry.ExpectedCalls.Length != 0 || call.IsStatic || call.RevertNextNCalls != 0)
        {
            throw new EezSettlementException("A DA action needs a flat, mutable call without expected calls.");
        }

        ulong targetRollupId = call.SourceRollupId == rollupId ? InboundDelivery.MainnetRollupId : rollupId;
        if (call.SourceRollupId != rollupId && entry.DestinationRollupId != rollupId)
        {
            throw new EezSettlementException($"A DA action's inbound entry targets rollup {entry.DestinationRollupId}, not {rollupId}.");
        }

        return new DaAction(call.SourceRollupId, targetRollupId, call.SourceAddress, call.TargetAddress, call.Value, call.Gas, call.Data,
            entry.Success, entry.ReturnData);
    }
}
