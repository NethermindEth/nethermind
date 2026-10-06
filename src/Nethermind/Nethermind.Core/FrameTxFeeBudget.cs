// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>The EIP-7999 aggregate <c>max_fee</c> budget of an EIP-8141 frame transaction.</summary>
/// <remarks>One budget covers execution, state and blob gas: no resource has its own per-gas cap.</remarks>
public static class FrameTxFeeBudget
{
    /// <summary>Prices the budget at the including block's base fees.</summary>
    /// <param name="maxFee">The transaction's <c>max_fee</c>.</param>
    /// <param name="maxPriorityFeePerGas">The transaction's <c>max_priority_fee_per_gas</c>.</param>
    /// <param name="maxGas">The gas the transaction reserves, <c>max_gas</c>.</param>
    /// <param name="baseFee">The block's EIP-1559 base fee.</param>
    /// <param name="blobFee">The transaction's blob gas priced at the block's EIP-4844 blob base fee.</param>
    /// <param name="requiredMaxFee"><c>max_gas * base_fee_per_gas + blob_gas * blob_base_fee</c>.</param>
    /// <param name="maxCost">What the payer is charged on approval, never above <paramref name="maxFee"/>.</param>
    /// <returns><c>false</c> when <paramref name="requiredMaxFee"/> overflows, which no <c>max_fee</c> covers.</returns>
    /// <remarks>The transaction is invalid when <paramref name="maxFee"/> is below <paramref name="requiredMaxFee"/>;
    /// a caller that skips that check (gas-free simulation) gets <paramref name="maxCost"/> clamped to <paramref name="maxFee"/>.</remarks>
    public static bool TryCalculate(in UInt256 maxFee, in UInt256 maxPriorityFeePerGas, ulong maxGas, in UInt256 baseFee, in UInt256 blobFee,
        out UInt256 requiredMaxFee, out UInt256 maxCost)
    {
        maxCost = UInt256.Zero;
        if (UInt256.MultiplyOverflow((UInt256)maxGas, baseFee, out requiredMaxFee)
            || UInt256.AddOverflow(requiredMaxFee, blobFee, out requiredMaxFee))
        {
            return false;
        }

        if (maxFee < requiredMaxFee)
        {
            maxCost = maxFee;
            return true;
        }

        UInt256 headroom = maxFee - requiredMaxFee;
        UInt256 maxPriorityFee = UInt256.MultiplyOverflow(maxPriorityFeePerGas, (UInt256)maxGas, out UInt256 tip)
            ? headroom
            : UInt256.Min(tip, headroom);
        maxCost = requiredMaxFee + maxPriorityFee;
        return true;
    }

    /// <summary>Splits the <paramref name="maxCost"/> collected on approval once <paramref name="gasUsed"/> is final.</summary>
    /// <param name="baseFeePaid"><c>gas_used * base_fee_per_gas</c>, burned.</param>
    /// <param name="blobFeePaid">The blob gas at the blob base fee, burned.</param>
    /// <param name="priorityFeePaid"><c>min(max_priority_fee_per_gas * gas_used, max_cost - base_fee_paid)</c>, to the coinbase,
    /// where the spec's <c>base_fee_paid</c> is <paramref name="baseFeePaid"/> plus <paramref name="blobFeePaid"/>.</param>
    /// <returns>The payer's refund, <c>max_cost - base_fee_paid - priority_fee_paid</c>.</returns>
    /// <remarks>Each leg saturates at what remains of <paramref name="maxCost"/>. A valid transaction never reaches
    /// the bound, as <c>gas_used &lt;= max_gas</c>; it only applies where validation was skipped.</remarks>
    public static UInt256 Settle(in UInt256 maxCost, in UInt256 maxPriorityFeePerGas, ulong gasUsed, in UInt256 baseFee, in UInt256 blobFee,
        out UInt256 baseFeePaid, out UInt256 blobFeePaid, out UInt256 priorityFeePaid)
    {
        UInt256 remaining = maxCost;
        baseFeePaid = UInt256.MultiplyOverflow((UInt256)gasUsed, baseFee, out UInt256 gasFee) ? remaining : UInt256.Min(gasFee, remaining);
        remaining -= baseFeePaid;
        blobFeePaid = UInt256.Min(blobFee, remaining);
        remaining -= blobFeePaid;
        priorityFeePaid = UInt256.MultiplyOverflow(maxPriorityFeePerGas, (UInt256)gasUsed, out UInt256 tip) ? remaining : UInt256.Min(tip, remaining);
        return remaining - priorityFeePaid;
    }

    /// <summary>The uniform per-gas price <paramref name="maxCost"/> escrows for execution and state gas.</summary>
    /// <remarks>What <c>GASPRICE</c> reads: <c>base_fee_per_gas</c> plus the escrowed tip spread over <c>max_gas</c>.
    /// EIP-7999 defines no per-gas price for the budget, so this is the price at which the escrow is spent in full.</remarks>
    public static UInt256 EffectiveGasPrice(in UInt256 maxCost, ulong maxGas, in UInt256 blobFee)
    {
        UInt256 gasCost = maxCost > blobFee ? maxCost - blobFee : UInt256.Zero;
        return maxGas == 0 ? gasCost : gasCost / maxGas;
    }
}
