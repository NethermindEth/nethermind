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

        INethermindApi CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer,
            ILogManager logManager, ChainSpec chainSpec) => new NethermindApi(configProvider, jsonSerializer, logManager, chainSpec);

        IBlockProducerRunner CreateBlockProducerRunner();
    }
}
