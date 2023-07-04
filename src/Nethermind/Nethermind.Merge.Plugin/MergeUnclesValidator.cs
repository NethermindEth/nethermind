// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public class MergeUnclesValidator : IUnclesValidator
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IUnclesValidator _preMergeUnclesValidator;

    public MergeUnclesValidator(
        IPoSSwitcher poSSwitcher,
        IUnclesValidator preMergeUnclesValidator)
    {
        _poSSwitcher = poSSwitcher;
        _preMergeUnclesValidator = preMergeUnclesValidator;
    }

    public bool Validate(BlockHeader header, BlockHeader[] uncles)
    {
        if (_poSSwitcher.IsPostMerge(header))
            return true;

        return _preMergeUnclesValidator.Validate(header, uncles);
    }
}
