// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    /// <summary>Filtering transactions that have lower MaxFeePerGas than BaseFee</summary>
    public class BaseFeeTxFilter : ITxFilter
    {
        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec)
        {
            UInt256 baseFee = BaseFeeCalculator.Calculate(parentHeader, spec);
            bool isEip1559Enabled = spec.IsEip1559Enabled;

            bool skipCheck = tx.IsServiceTransaction || !isEip1559Enabled;
            bool allowed = skipCheck || tx.MaxFeePerGas >= baseFee;
            return allowed
                ? AcceptTxResult.Accepted
                : AcceptTxResult.FeeTooLow.WithMessage(
                    $"MaxFeePerGas too low. MaxFeePerGas: {tx.MaxFeePerGas}, BaseFee: {baseFee}, MaxPriorityFeePerGas:{tx.MaxPriorityFeePerGas}, Block number: {parentHeader.Number + 1}");
        }
    }
}
