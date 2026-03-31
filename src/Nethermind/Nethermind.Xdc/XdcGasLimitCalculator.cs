// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal class XdcGasLimitCalculator(ISpecProvider specProvider, IBlocksConfig blocksConfig) : IGasLimitCalculator
{
    private readonly TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new TargetAdjustedGasLimitCalculator(specProvider, blocksConfig);
    public long GetGasLimit(BlockHeader parentHeader)
    {
        IXdcReleaseSpec spec = specProvider.GetXdcSpec(parentHeader.Number + 1);
        if (spec.IsDynamicGasLimitBlock)
        {
            return targetAdjustedGasLimitCalculator.GetGasLimit(parentHeader);
        }
        return blocksConfig.TargetBlockGasLimit ?? XdcConstants.DefaultTargetGasLimit;
    }
}
