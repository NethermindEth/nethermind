// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// EIP-7805 (FOCIL) IL satisfaction check. Validates against PARENT-block state plus the
/// effects of any IL txs the block already includes; post-execution state would let a
/// censoring builder defeat the IL with a same-nonce replacement tx.
/// </summary>
public static class InclusionListValidator
{
    public static bool IsSatisfied(
        Block block,
        IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState,
        IReleaseSpec spec)
    {
        if (!spec.InclusionListsEnabled)
        {
            return true;
        }

        if (block.InclusionListTransactions is null)
        {
            return false;
        }

        // FOCIL is conditional: no gas left for a base-cost transfer → nothing is appendable.
        if (block.GasUsed + Transaction.BaseTxGasCost > block.GasLimit)
        {
            return true;
        }

        Transaction[] il = block.InclusionListTransactions;
        HashSet<Transaction> includedTxs = new(block.Transactions, ByHashTxComparer.Instance);

        // Inline `Contains` once per IL tx into a bitmap so the second pass below doesn't hash
        // again. The spec cap (Eip7805Constants.MaxTransactionsPerInclusionList = 256) is small
        // enough to always stackalloc.
        Span<bool> included = il.Length <= Eip7805Constants.MaxTransactionsPerInclusionList
            ? stackalloc bool[il.Length]
            : new bool[il.Length];

        // Accumulate per-sender deltas for the IL txs the block already includes — per spec the
        // appendability check for the others runs against parent state plus those. Worst-case
        // cost (gasLimit * maxFeePerGas + value) matches TransactionProcessor's pre-check.
        Dictionary<AddressAsKey, IlDelta> ilDeltas = new(il.Length);
        for (int i = 0; i < il.Length; i++)
        {
            Transaction tx = il[i];
            if (!includedTxs.Contains(tx)) continue;
            included[i] = true;

            if (tx.SenderAddress is null) continue;
            UInt256 cost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
            ref IlDelta slot = ref CollectionsMarshal.GetValueRefOrAddDefault(ilDeltas, tx.SenderAddress, out _);
            slot = new IlDelta(slot.IncludedCount + 1, slot.Cost + cost);
        }

        for (int i = 0; i < il.Length; i++)
        {
            if (!included[i] && CouldIncludeTx(il[i], block, parentSenderState, ilDeltas))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CouldIncludeTx(
        Transaction tx,
        Block block,
        IReadOnlyDictionary<AddressAsKey, AccountSnapshot> parentSenderState,
        Dictionary<AddressAsKey, IlDelta> ilDeltas)
    {
        // Null sender (recovery failed) → not-appendable, also avoids NRE on the lookup below.
        if (tx.SenderAddress is null)
        {
            return false;
        }

        if (block.GasUsed + tx.GasLimit > block.GasLimit)
        {
            return false;
        }

        // EIP-1559: compare baseFee against the cap (MaxFeePerGas), not the priority tip
        // (which is what tx.GasPrice exposes for type-2). Matches TransactionProcessor.
        if (tx.MaxFeePerGas < block.BaseFeePerGas)
        {
            return false;
        }

        AccountSnapshot baseAcc = parentSenderState.GetValueOrDefault(tx.SenderAddress, AccountSnapshot.Empty);
        ilDeltas.TryGetValue(tx.SenderAddress, out IlDelta delta);

        if (tx.Nonce != baseAcc.Nonce + delta.IncludedCount)
        {
            return false;
        }

        UInt256 remainingBalance = baseAcc.Balance >= delta.Cost ? baseAcc.Balance - delta.Cost : UInt256.Zero;
        UInt256 txCost = tx.Value + (UInt256)tx.GasLimit * tx.MaxFeePerGas;
        return remainingBalance >= txCost;
    }

    private readonly record struct IlDelta(ulong IncludedCount, UInt256 Cost);
}

/// <summary>
/// Account nonce + balance captured before block processing, so EIP-7805's IL satisfaction
/// check can read parent state rather than the live post-execution worldstate.
/// </summary>
public readonly record struct AccountSnapshot(UInt256 Balance, UInt256 Nonce)
{
    public static readonly AccountSnapshot Empty = default;
}
