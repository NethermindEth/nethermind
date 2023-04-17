// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin
    {
        /// <summary>
        /// Creates a block producer.
        /// </summary>
        /// <param name="blockProductionTrigger">Optional parameter. If present this should be the only block production trigger for created block producer. If absent <see cref="DefaultBlockProductionTrigger"/> should be used.</param>
        /// <param name="additionalTxSource">Optional parameter. If present this transaction source should be used before any other transaction sources, except consensus ones. Plugin still should use their own transaction sources.</param>
        /// <remarks>
        /// Can be called many times, with different parameters, each time should create a new instance. Example usage in MEV plugin.
        /// </remarks>
        Task<IBlockProducer> InitBlockProducer(
            IBlockProductionTrigger? blockProductionTrigger = null,
            ITxSource? additionalTxSource = null);

        string SealEngineType { get; }

        /// <summary>
        /// Default block production trigger for this consensus plugin.
        /// </summary>
        /// <remarks>
        /// Needed when this plugin is used in combination with other plugin that affects block production like MEV plugin.
        /// </remarks>
        IBlockProductionTrigger DefaultBlockProductionTrigger { get; }

        INethermindApi CreateApi() => new NethermindApi();
    }
}
