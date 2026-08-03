// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Xdc;

internal class XdcGasLimitCalculator(ISpecProvider specProvider, IBlocksConfig blocksConfig) : IGasLimitCalculator
{
    private readonly TargetAdjustedGasLimitCalculator targetAdjustedGasLimitCalculator = new(specProvider, blocksConfig);
    public ulong GetGasLimit(BlockHeader parentHeader, ulong? targetGasLimit = null) =>
        specProvider.GetXdcSpec(parentHeader.Number + 1).IsDynamicGasLimitBlock
            ? targetAdjustedGasLimitCalculator.GetGasLimit(parentHeader, targetGasLimit)
            : blocksConfig.TargetBlockGasLimit ?? XdcConstants.DefaultTargetGasLimit;
}
