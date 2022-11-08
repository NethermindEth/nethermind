//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            IReleaseSpec specFor1559 = _specProvider.GetSpecFor1559(blockNumber);
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
