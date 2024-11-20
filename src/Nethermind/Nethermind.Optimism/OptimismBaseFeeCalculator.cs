// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Optimism;

/// <remarks>
/// See <see href="https://specs.optimism.io/protocol/holocene/exec-engine.html#base-fee-computation"/>
/// </remarks>
public sealed class OptimismBaseFeeCalculator(
    IBaseFeeCalculator baseFeeCalculator,
    ISpecProvider specProvider) : IBaseFeeCalculator
{
    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
    {
        var spec = specFor1559;

        var releaseSpec = specProvider.GetSpec(parent);
        if (releaseSpec.IsOpHoloceneEnabled)
        {
            // NOTE: This operation should never fail since headers should be valid at this point
            EIP1559Parameters eip1559Params = parent.DecodeEIP1559Parameters();
            spec = eip1559Params.IsZero()
                ? new Eip1559Spec(specFor1559)
                {
                    ElasticityMultiplier = Eip1559Constants.DefaultElasticityMultiplier,
                    BaseFeeMaxChangeDenominator = Eip1559Constants.DefaultBaseFeeMaxChangeDenominator
                }
                : new Eip1559Spec(specFor1559)
                {
                    ElasticityMultiplier = eip1559Params.Elasticity,
                    BaseFeeMaxChangeDenominator = eip1559Params.Denominator
                };
        }

        return baseFeeCalculator.Calculate(parent, spec);
    }
}
