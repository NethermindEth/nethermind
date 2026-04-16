// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Blockchain.Services
{
    public class HealthHintService(ChainSpec chainSpec) : IHealthHintService
    {
        private readonly ChainSpec _chainSpec = chainSpec;

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            ulong? blockProcessorHint;
            if (_chainSpec.SealEngineType == SealEngineType.Ethash)
                blockProcessorHint = HealthHintConstants.EthashStandardProcessingPeriod * HealthHintConstants.EthashProcessingSafetyMultiplier;
            else
                blockProcessorHint = HealthHintConstants.InfinityHint;

            return blockProcessorHint;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint() => HealthHintConstants.InfinityHint;
    }
}
