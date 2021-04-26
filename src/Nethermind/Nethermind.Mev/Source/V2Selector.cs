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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;

namespace Nethermind.Mev.Source
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
        
        public async Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit)
        {
            SimulatedMevBundle? bestBundle = null;
            UInt256 bestMevEquivalentPrice = 0;
            long totalGasUsed = 0;
            long maxGasUsed = gasLimit * _maxBundlesGasUsedRatio / 100;
            
            IEnumerable<MevBundle> bundles = await _bundleSource.GetBundles(parent, timestamp, gasLimit);
            IEnumerable<SimulatedMevBundle> simulatedBundles = await _bundleSimulator.Simulate(bundles, parent, gasLimit);
            
            foreach (SimulatedMevBundle simulatedBundle in simulatedBundles)
            {
                if (maxGasUsed - totalGasUsed >= GasCostOf.Transaction)
                {
                    UInt256 tailGas = _tailGasPriceCalculator.Calculate(parent, 0, simulatedBundle.GasUsed);
                    if (simulatedBundle.MevEquivalentGasPrice > bestMevEquivalentPrice
                        && simulatedBundle.MevEquivalentGasPrice > tailGas)
                    {
                        if (simulatedBundle.GasUsed + totalGasUsed <= maxGasUsed)
                        {
                            totalGasUsed += simulatedBundle.GasUsed;
                            bestMevEquivalentPrice = simulatedBundle.MevEquivalentGasPrice;
                            bestBundle = simulatedBundle;
                        }
                    }
                }
            }

            return bestBundle is null ? Enumerable.Empty<MevBundle>() : Enumerable.Repeat(bestBundle.Bundle, 1);
        }
    }
}
