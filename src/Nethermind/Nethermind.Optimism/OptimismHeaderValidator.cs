// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismHeaderValidator : HeaderValidator
{
    public OptimismHeaderValidator(IBlockTree? blockTree, ISealValidator? sealValidator, ISpecProvider? specProvider, ILogManager? logManager) : base(blockTree, sealValidator, specProvider, logManager)
    {
    }

    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec) => true;
}
