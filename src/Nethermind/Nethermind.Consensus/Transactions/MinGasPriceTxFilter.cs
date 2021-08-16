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
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Transactions
{
    /// <summary>The filter for transactions below minimum gas price threshold. It is the minimal value for gas that the miner/validator would receive.
    /// Before 1559: EffectivePriorityFeePerGas = transaction.GasPrice.
    /// After 1559: EffectivePriorityFeePerGas = transaction.EffectiveGasPrice - BaseFee.</summary>
    public class MinGasPriceTxFilter : IMinGasPriceTxFilter
    {
        private readonly UInt256 _minGasPrice;
        private readonly ISpecProvider _specProvider;

        public MinGasPriceTxFilter(
            UInt256 minGasPrice,
            ISpecProvider specProvider)
        {
            _minGasPrice = minGasPrice;
            _specProvider = specProvider;
        }

        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader parentHeader)
        {
            return IsAllowed(tx, parentHeader, _minGasPrice);
        }

        public (bool Allowed, string Reason) IsAllowed(Transaction tx, BlockHeader? parentHeader, UInt256 minGasPriceFloor)
        {
            UInt256 premiumPerGas = tx.GasPrice;
            UInt256 baseFeePerGas = UInt256.Zero;
            long blockNumber = (parentHeader?.Number ?? 0) + 1;
            IReleaseSpec spec = _specProvider.GetSpec(blockNumber);
            if (spec.IsEip1559Enabled)
            {
                baseFeePerGas = BaseFeeCalculator.Calculate(parentHeader, spec);
                tx.TryCalculatePremiumPerGas(baseFeePerGas, out premiumPerGas);
            }

            bool allowed = premiumPerGas >= minGasPriceFloor;
            return (allowed, allowed ? string.Empty : $"EffectivePriorityFeePerGas too low {premiumPerGas} < {minGasPriceFloor}, BaseFee: {baseFeePerGas}");
        }
    }
}
