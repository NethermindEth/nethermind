// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public class MegabundleSelector : IBundleSource
    {
        private readonly ISimulatedBundleSource _simulatedBundleSource;

        public MegabundleSelector(ISimulatedBundleSource simulatedBundleSource)
        {
            _simulatedBundleSource = simulatedBundleSource;
        }

        public async Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit,
            CancellationToken token = default)
        {
            IEnumerable<SimulatedMevBundle> simulatedBundles = await _simulatedBundleSource.GetMegabundles(parent, timestamp, gasLimit, token);
            return simulatedBundles
                .OrderByDescending(s => s.BundleAdjustedGasPrice)
                .ThenBy(s => s.Bundle.SequenceNumber)
                .Take(1)
                .Select(s => s.Bundle);
        }
    }
}
