// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin, IBlockProducerFactory
    {
        INethermindApi CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer,
            ILogManager logManager, ChainSpec chainSpec) => new NethermindApi(configProvider, jsonSerializer, logManager, chainSpec);

        IBlockProducerRunner CreateBlockProducerRunner();
    }
}
