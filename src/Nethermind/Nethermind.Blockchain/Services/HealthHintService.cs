// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Blockchain.Services
{
    public class HealthHintService : IHealthHintService
    {
        private readonly ChainSpec _chainSpec;

        public HealthHintService(ChainSpec chainSpec)
        {
            _chainSpec = chainSpec;
        }

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            ulong? blockProcessorHint;
            if (_chainSpec.SealEngineType == SealEngineType.Ethash)
                blockProcessorHint = HealthHintConstants.EthashStandardProcessingPeriod * HealthHintConstants.EthashProcessingSafetyMultiplier;
            else
                blockProcessorHint = HealthHintConstants.InfinityHint;

            return blockProcessorHint;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return HealthHintConstants.InfinityHint;
        }
    }
}
