// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusWrapperPlugin : INethermindPlugin
    {
        Task<IBlockProducer> InitBlockProducer(IBlockProducerFactory baseBlockProducerFactory, IBlockProductionTrigger blockProductionTrigger, ITxSource? txSource);

        /// <summary>
        /// Priorities for ordering multiple plugin. Only used to determine the wrapping order of block production.
        /// </summary>
        int Priority => 0;

        bool Enabled { get; }
    }
}
