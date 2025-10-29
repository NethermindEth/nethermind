// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Optimism;

/// <remarks>
/// See <see href="https://specs.optimism.io/protocol/holocene/exec-engine.html#base-fee-computation"/>
/// </remarks>
public sealed class OptimismBaseFeeCalculator(
    ulong holoceneTimestamp,
    IBaseFeeCalculator baseFeeCalculator
) : IBaseFeeCalculator
{
    public UInt256 Calculate(BlockHeader parent, IEip1559Spec specFor1559)
    {
        var spec = specFor1559;
        EIP1559Parameters eip1559Params = default;

        if (parent.Timestamp >= holoceneTimestamp)
        {
            // NOTE: This operation should never fail since headers should be valid at this point.
            if (!parent.TryDecodeEIP1559Parameters(out eip1559Params, out var error))
            {
                throw new InvalidOperationException($"{nameof(BlockHeader)} was not properly validated: {error}");
            }

            spec = new OverridableEip1559Spec(specFor1559)
            {
                ElasticityMultiplier = eip1559Params.Elasticity,
                BaseFeeMaxChangeDenominator = eip1559Params.Denominator
            };
        }

        UInt256 baseFee = baseFeeCalculator.Calculate(parent, spec);

        if (eip1559Params.MinBaseFee > 0)
            baseFee = UInt256.Max(baseFee, eip1559Params.MinBaseFee);

        return baseFee;
    }
}
