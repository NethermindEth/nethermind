// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
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
            ILogManager logManager,
            IEthereumEcdsa ecdsa)
            : base(blockTree, simulator, timestamper, txValidator, specProvider, mevConfig, new MockProvider(), logManager, ecdsa)
        {
        }

        protected override SimulatedMevBundleContext? SimulateBundle(MevBundle bundle, BlockHeader parent)
        {
            SimulatedMevBundleContext? simulatedMevBundleContext = base.SimulateBundle(bundle, parent);
            _queue.Add((bundle, simulatedMevBundleContext));
            return simulatedMevBundleContext;
        }

        public Task<bool?> WaitForSimulationToFinish(MevBundle bundle, CancellationToken token) => WaitForSimulation(true, bundle, token);

        public Task WaitForSimulationToStart(MevBundle bundle, CancellationToken token) => WaitForSimulation(false, bundle, token);

        private async Task<bool?> WaitForSimulation(bool toFinish, MevBundle bundle, CancellationToken token)
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
                        SimulatedMevBundle simulatedMevBundle = await simulatedBundle.Item2.Task;
                        return simulatedMevBundle.Success;
                    }

                    break;
                }
                else
                {
                    _queue.Add(simulatedBundle, token);
                }
            }

            return null;
        }
    }

    public class MockProvider : IAccountStateProvider
    {
        public Account GetAccount(Address address) => new Account(0);
    }
}
