// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Mev.Data;

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

            HashSet<Hash256> selectedTransactionsHashes = new HashSet<Hash256>();

            foreach (SimulatedMevBundle simulatedBundle in simulatedBundles.OrderByDescending(bundle => bundle.BundleAdjustedGasPrice).ThenBy(bundle => bundle.Bundle.SequenceNumber))
            {
                if (numBundles < _bundleLimit)
                {
                    if (simulatedBundle.GasUsed <= gasLimit - totalGasUsed)
                    {
                        IEnumerable<Hash256> bundleTransactionHashes = simulatedBundle.Bundle.Transactions.Select(tx => tx.Hash!);
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
