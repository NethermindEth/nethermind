// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Execution
{
    public interface IBundleSimulator
    {
        Task<SimulatedMevBundle> Simulate(MevBundle bundle, BlockHeader parent, CancellationToken cancellationToken = default);

        // Todo add timeout
        public async Task<IEnumerable<SimulatedMevBundle>> Simulate(IEnumerable<MevBundle> bundles, BlockHeader parent, CancellationToken cancellationToken = default)
        {
            List<Task<SimulatedMevBundle>> simulations = new();
            foreach (MevBundle bundle in bundles)
            {
                simulations.Add(Simulate(bundle, parent, cancellationToken));
            }

            return await Task.WhenAll(simulations);
        }

    }
}
