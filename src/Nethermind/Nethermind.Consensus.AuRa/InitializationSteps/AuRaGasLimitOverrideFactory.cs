// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Abi;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaGasLimitOverrideFactory(
    AuRaChainSpecEngineParameters parameters,
    ISpecProvider specProvider,
    ILogManager logManager,
    IAuraConfig auraConfig,
    IBlocksConfig blocksConfig,
    IAbiEncoder abiEncoder,
    IReadOnlyTxProcessingEnvFactory envFactory,
    AuRaContractGasLimitOverride.Cache gasLimitOverrideCache)
{
    public AuRaContractGasLimitOverride? GetGasLimitCalculator()
    {
        var blockGasLimitContractTransitions = parameters.BlockGasLimitContractTransitions;

        if (blockGasLimitContractTransitions?.Any() == true)
        {
            AuRaContractGasLimitOverride gasLimitCalculator = new(
                blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            abiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            envFactory.Create()))
                    .ToArray<IBlockGasLimitContract>(),
                gasLimitOverrideCache,
                auraConfig.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                new TargetAdjustedGasLimitCalculator(specProvider, blocksConfig),
                logManager);

            return gasLimitCalculator;
        }

        // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
        return null;
    }
}
