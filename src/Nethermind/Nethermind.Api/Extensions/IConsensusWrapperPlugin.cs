// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusWrapperPlugin : INethermindPlugin
    {
        IBlockProducer InitBlockProducer(IBlockProducerFactory baseBlockProducerFactory, ITxSource? txSource);

        IBlockProducerRunner InitBlockProducerRunner(IBlockProducerRunner baseRunner) => baseRunner;

        /// <summary>
        /// Priorities for ordering multiple plugin. Only used to determine the wrapping order of block production.
        /// </summary>
        int Priority => 0;

        bool Enabled { get; }
    }
}
