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

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.TxPool;

namespace Nethermind.Mev.Test
{
    public class TestBundlePool : BundlePool
    {
        private BlockingCollection<(MevBundle Bundle, SimulatedMevBundleContext? Context)> _queue = new(new ConcurrentQueue<(MevBundle, SimulatedMevBundleContext?)>());
        
        public TestBundlePool(IBlockTree blockTree, 
            IBundleSimulator simulator,
            ITimestamper timestamper,
            ITxValidator txValidator, 
            ISpecProvider specProvider,
            IMevConfig mevConfig,
            ILogManager logManager)
            : base(blockTree, simulator, timestamper, txValidator, specProvider, mevConfig, logManager)
        {
        }

        protected override SimulatedMevBundleContext? SimulateBundle(MevBundle bundle, BlockHeader parent)
        {
            SimulatedMevBundleContext? simulatedMevBundleContext = base.SimulateBundle(bundle, parent);
            _queue.Add((bundle, simulatedMevBundleContext));
            return simulatedMevBundleContext;
        }

        public Task WaitForSimulationToFinish(MevBundle bundle, CancellationToken token) => WaitForSimulation(true, bundle, token);

        public Task WaitForSimulationToStart(MevBundle bundle, CancellationToken token) => WaitForSimulation(false, bundle, token);

        private async Task WaitForSimulation(bool toFinish, MevBundle bundle, CancellationToken token)
        {
            foreach ((MevBundle Bundle, SimulatedMevBundleContext? Context) simulatedBundle in _queue.GetConsumingEnumerable(token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (bundle.Hash == simulatedBundle.Bundle.Hash)
                {
                    if (toFinish && simulatedBundle.Context is not null)
                    {
                        await simulatedBundle.Item2.Task;
                    }

                    break;
                }
                else
                {
                    _queue.Add(simulatedBundle, token);
                }
            }
        }
    }
}
