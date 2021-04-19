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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public class V2Selector : IBundleSource
    {
        private readonly IBundleSource _bundleSource;
        
        private readonly IBundleSimulator _bundleSimulator;

        private readonly ITailGasPriceCalculator _tailGasPriceCalculator;
        private readonly long _maxBundlesGasUsedRatio;

        public V2Selector(
            IBundleSource bundleSource,
            IBundleSimulator bundleSimulator,
            ITailGasPriceCalculator tailGasPriceCalculator,
            long maxBundlesGasUsedRatio = 100)
        {
            _bundleSource = bundleSource;
            _bundleSimulator = bundleSimulator;
            _tailGasPriceCalculator = tailGasPriceCalculator;
            _maxBundlesGasUsedRatio = maxBundlesGasUsedRatio;
        }
        
        public IEnumerable<MevBundle> GetBundles(BlockHeader parent, long gasLimit)
        {
            MevBundle? bestBundle = null;
            UInt256 bestMevEquivalentPrice = 0;
            long totalGasUsed = 0;
            long maxGasUsed = gasLimit * _maxBundlesGasUsedRatio / 100;
            foreach (var bundle in _bundleSource.GetBundles(parent, gasLimit))
            {
                if (maxGasUsed - totalGasUsed < 21000)
                {
                    break;
                }
                
                SimulatedMevBundle simulatedMevBundle = _bundleSimulator.Simulate(parent, gasLimit, bundle);
                UInt256 tailGas = _tailGasPriceCalculator.Calculate(parent, 0, simulatedMevBundle.GasUsed);
                if (simulatedMevBundle.MevEquivalentGasPrice > bestMevEquivalentPrice 
                    && simulatedMevBundle.MevEquivalentGasPrice > tailGas)
                {
                    if (simulatedMevBundle.GasUsed + totalGasUsed <= maxGasUsed)
                    {
                        totalGasUsed += simulatedMevBundle.GasUsed;
                        bestMevEquivalentPrice = simulatedMevBundle.MevEquivalentGasPrice;
                        bestBundle = bundle;
                    }
                }
            }

            if (bestBundle is null)
            {
                return Enumerable.Empty<MevBundle>();
            }

            return Enumerable.Repeat(bestBundle, 1);
        }
    }
}
