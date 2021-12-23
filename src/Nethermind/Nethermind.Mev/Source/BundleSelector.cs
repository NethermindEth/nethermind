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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;

namespace Nethermind.Mev.Source
{
    public class BundleSelector : IBundleSource
    {
        private readonly ISimulatedBundleSource _simulatedBundleSource;
        private readonly int _bundleLimit;
        

        public BundleSelector(
            ISimulatedBundleSource simulatedBundleSource,
            int bundleLimit)
        {
            _simulatedBundleSource = simulatedBundleSource;
            _bundleLimit = bundleLimit;
        }
        
        public async Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token = default)
        {
            IEnumerable<SimulatedMevBundle> simulatedBundles = await _simulatedBundleSource.GetBundles(parent, timestamp, gasLimit, token);
            return FilterBundles(simulatedBundles, gasLimit);
        }

        private IEnumerable<MevBundle> FilterBundles(IEnumerable<SimulatedMevBundle> simulatedBundles, long gasLimit)
        {
            long totalGasUsed = 0;
            int numBundles = 0;

            HashSet<Keccak> selectedTransactionsHashes = new HashSet<Keccak>();

            foreach (SimulatedMevBundle simulatedBundle in simulatedBundles.OrderByDescending(bundle => bundle.BundleAdjustedGasPrice).ThenBy(bundle => bundle.Bundle.SequenceNumber))
            {
                if (numBundles < _bundleLimit)
                {
                    if (simulatedBundle.GasUsed <= gasLimit - totalGasUsed)
                    {
                        IEnumerable<Keccak> bundleTransactionHashes = simulatedBundle.Bundle.Transactions.Select(tx => tx.Hash!);
                        if (!selectedTransactionsHashes.Overlaps(bundleTransactionHashes))
                        {
                            totalGasUsed += simulatedBundle.GasUsed;
                            numBundles++;

                            selectedTransactionsHashes.UnionWith(bundleTransactionHashes);
                            yield return simulatedBundle.Bundle;
                        }
                    }
                }
                else
                {
                    yield break;
                }
            }
        }
    }
}
