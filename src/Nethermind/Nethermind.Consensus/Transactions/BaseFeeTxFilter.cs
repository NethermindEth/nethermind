// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    /// <summary>Filtering transactions that have lower MaxFeePerGas than BaseFee</summary>
    /// <param name="slots">
    /// Places the next block for the EIP-8198 base fee, in the slot after the parent's; without it a 12 s slot is assumed.
    /// </param>
    public class BaseFeeTxFilter(SlotSchedule? slots = null) : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, in TxFilterContext context)
        {
            bool isEip1559Enabled = context.Spec.IsEip1559Enabled;

            if (!isEip1559Enabled || tx.IsServiceTransaction)
            {
                return AcceptTxResult.Accepted;
            }

            UInt256 baseFee = context.GetBaseFee(slots);
            bool allowed = tx.MaxFeePerGas >= baseFee;
            return allowed
                ? AcceptTxResult.Accepted
                : AcceptTxResult.FeeTooLow.WithMessage(
                    $"MaxFeePerGas too low. MaxFeePerGas: {tx.MaxFeePerGas}, BaseFee: {baseFee}, MaxPriorityFeePerGas:{tx.MaxPriorityFeePerGas}, Block number: {context.Parent.Number + 1}");
        }
    }
}
