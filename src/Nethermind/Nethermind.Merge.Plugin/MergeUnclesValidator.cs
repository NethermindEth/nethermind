// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public class MergeUnclesValidator(
    IPoSSwitcher poSSwitcher,
    IUnclesValidator preMergeUnclesValidator) : IUnclesValidator
{
    private readonly IPoSSwitcher _poSSwitcher = poSSwitcher;
    private readonly IUnclesValidator _preMergeUnclesValidator = preMergeUnclesValidator;

    public bool Validate(BlockHeader header, BlockHeader[] uncles)
    {
        if (_poSSwitcher.IsPostMerge(header))
            return true;

        return _preMergeUnclesValidator.Validate(header, uncles);
    }
}
