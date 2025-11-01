// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Optimism.ExtraParams;

namespace Nethermind.Optimism;

/// <remarks>
/// See <see href="https://specs.optimism.io/protocol/holocene/exec-engine.html#base-fee-computation"/>
/// </remarks>
public sealed class OptimismBaseFeeCalculator(
    ulong holoceneTimestamp,
    // ulong jovianTimestamp,
    IBaseFeeCalculator baseFeeCalculator
) : IBaseFeeCalculator
{
    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
    {
        var spec = specFor1559;

        if (parent.Timestamp >= holoceneTimestamp)
        {
            // NOTE: This operation should never fail since headers should be valid at this point.
            if (!HoloceneExtraParams.TryParse(parent, out HoloceneExtraParams parameters, out var error))
            {
                throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
            }

            spec = new OverridableEip1559Spec(specFor1559)
            {
                ElasticityMultiplier = parameters.Elasticity,
                BaseFeeMaxChangeDenominator = parameters.Denominator
            };
        }

        if (false /* parent.Timestamp >= jovianTimestamp */)
#pragma warning disable CS0162 // Unreachable code detected
        {
            // NOTE: This operation should never fail since headers should be valid at this point.
            if (!JovianExtraParams.TryParse(parent, out JovianExtraParams eip1559Params, out var error))
            {
                throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
            }

            spec = new OverridableEip1559Spec(specFor1559)
            {
                ElasticityMultiplier = eip1559Params.Elasticity,
                BaseFeeMaxChangeDenominator = eip1559Params.Denominator
            };
        }
#pragma warning restore CS0162 // Unreachable code detected

        return baseFeeCalculator.Calculate(parent, spec);
    }
}
