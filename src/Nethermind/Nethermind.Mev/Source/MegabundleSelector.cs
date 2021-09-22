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
                .OrderByDescending(bundle => bundle.BundleAdjustedGasPrice)
                .ThenBy(bundle => bundle.Bundle.SequenceNumber)
                .Take(1)
                .Select(s => s.Bundle);
        }
    }
}
