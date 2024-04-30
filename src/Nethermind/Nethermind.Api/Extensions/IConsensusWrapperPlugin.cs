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

        bool Enabled { get; }
    }
}
