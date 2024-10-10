// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismHeaderValidator(
    IBlockTree? blockTree,
    ISealValidator? sealValidator,
    ISpecProvider? specProvider,
    IOptimismSpecHelper specHelper,
    ILogManager? logManager)
    : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;

    protected override bool ValidateTotalDifficulty(BlockHeader parent, BlockHeader header, ref string? error)
    {
        if (specHelper.IsBedrock(header))
            return (header.TotalDifficulty ?? 0) == 0;

        return base.ValidateTotalDifficulty(parent, header, ref error);
    }
}
