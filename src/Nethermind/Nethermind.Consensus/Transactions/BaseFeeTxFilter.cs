// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Transactions
{
    /// <summary>Filtering transactions that have lower MaxFeePerGas than BaseFee</summary>
    public class BaseFeeTxFilter : ITxFilter
    {
        private readonly ISpecProvider _specProvider;

        public BaseFeeTxFilter(
            ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            long blockNumber = parentHeader.Number + 1;
            IEip1559Spec specFor1559 = _specProvider.GetSpecFor1559(blockNumber);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parentHeader, specFor1559);
            bool isEip1559Enabled = specFor1559.IsEip1559Enabled;

            bool skipCheck = tx.IsServiceTransaction || !isEip1559Enabled;
            bool allowed = skipCheck || tx.MaxFeePerGas >= baseFee;
            return allowed
                ? AcceptTxResult.Accepted
                : AcceptTxResult.FeeTooLow.WithMessage(
                    $"MaxFeePerGas too low. MaxFeePerGas: {tx.MaxFeePerGas}, BaseFee: {baseFee}, MaxPriorityFeePerGas:{tx.MaxPriorityFeePerGas}, Block number: {blockNumber}");
        }
    }
}
