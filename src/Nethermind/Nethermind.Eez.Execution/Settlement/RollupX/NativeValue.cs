// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class NativeValue
{
    private static readonly UInt256 MaxSigned = (UInt256.One << 255) - 1;
    private static readonly UInt256 NegativeLimit = UInt256.One << 191;

    public static UInt256 Funding(ReadOnlySpan<L2ExecutionEntry> entries)
    {
        UInt256 value = 0;
        foreach (L2ExecutionEntry entry in entries)
        {
            foreach (CrossChainCall call in entry.IncomingCalls) value = Add(value, call.Value);
            foreach (ExpectedCall row in entry.ExpectedOutgoingCalls)
                foreach (CrossChainCall call in row.Calls) value = Add(value, call.Value);
        }
        return value;
    }

    public static SignedInt256 Delta(NativeCallTree tree, NativeCallTree.Node? root = null)
    {
        UInt256 inflow = 0;
        UInt256 outflow = 0;
        (UInt256 Inflow, UInt256 Outflow, int End) saved = default;
        int end = root?.SubtreeEnd ?? tree.Calls.Count;
        for (int index = root?.Index ?? 0; index < end; index++)
        {
            NativeCallTree.Node call = tree.Calls[index];
            if (call.RevertEnd != 0) saved = (inflow, outflow, call.RevertEnd);
            if (!call.Encoded.IsStatic)
            {
                bool incoming = call.Encoded.SourceRollupId == 0;
                UInt256 next = Add(incoming ? inflow : outflow, call.Encoded.Value);
                if (call.Result.Success)
                {
                    if (incoming) inflow = next;
                    else outflow = next;
                }
            }
            if (index + 1 == saved.End) (inflow, outflow) = (saved.Inflow, saved.Outflow);
        }
        if (inflow > MaxSigned || outflow > MaxSigned) throw Invalid("value projection exceeds int256");
        if (inflow >= outflow)
        {
            UInt256.Subtract(inflow, outflow, out UInt256 credit);
            return EtherDelta.Credit(credit);
        }
        UInt256.Subtract(outflow, inflow, out UInt256 debit);
        if (debit > NegativeLimit) throw Invalid("value projection exceeds int192");
        SignedInt256.Subtract(SignedInt256.Zero, new SignedInt256(debit), out SignedInt256 delta);
        return delta;
    }

    private static UInt256 Add(in UInt256 left, in UInt256 right) =>
        UInt256.AddOverflow(left, right, out UInt256 sum) ? throw Invalid("value projection exceeds uint256") : sum;

    private static EezSettlementException Invalid(string reason) => NativeCallTree.Invalid(reason);
}
