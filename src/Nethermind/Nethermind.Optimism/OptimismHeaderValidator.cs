// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;

namespace Nethermind.Optimism;

public class PreBedrockHeaderValidator(
    IBlockTree? blockTree,
    ISealValidator? sealValidator,
    ISpecProvider? specProvider,
    ILogManager? logManager) : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
{
    public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        error = null;
        return ValidateParent(header, parent, ref error);
    }
}

public class OptimismHeaderValidator(
    IPoSSwitcher poSSwitcher,
    IBlockTree blockTree,
    ISealValidator sealValidator,
    ISpecProvider specProvider,
    ILogManager logManager)
    : MergeHeaderValidator(
        poSSwitcher,
        new PreBedrockHeaderValidator(blockTree, sealValidator, specProvider, logManager),
        blockTree, specProvider, sealValidator, logManager)
{
    public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error)
    {
        IReleaseSpec spec = _specProvider.GetSpec(header);
        if (spec.IsOpHoloceneEnabled)
        {
            try
            {
                // TODO: We might want to avoid using exceptions here
                _ = header.DecodeEIP1559Parameters();
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return false;
            }
        }

        return base.Validate(header, parent, isUncle, out error);
    }

    protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string? error) => true;
}
