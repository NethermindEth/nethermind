// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public interface ISimulatedBundleSource
    {
        Task<IEnumerable<SimulatedMevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit,
            CancellationToken token = default);

        Task<IEnumerable<SimulatedMevBundle>> GetMegabundles(BlockHeader parent, UInt256 timestamp, long gasLimit,
            CancellationToken token = default);
    }
}
