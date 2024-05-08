// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin, IBlockProducerFactory
    {
        string SealEngineType { get; }

        /// <summary>
        /// Default block production trigger for this consensus plugin.
        /// </summary>
        /// <remarks>
        /// Needed when this plugin is used in combination with other plugin that affects block production like MEV plugin.
        /// </remarks>
        IBlockProductionTrigger DefaultBlockProductionTrigger { get; }

        INethermindApi CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer,
            ILogManager logManager, ChainSpec chainSpec) => new NethermindApi(configProvider, jsonSerializer, logManager, chainSpec);
    }
}
