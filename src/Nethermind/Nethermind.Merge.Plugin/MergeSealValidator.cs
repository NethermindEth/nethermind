// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Merge.Plugin;

public class MergeSealValidator : ISealValidator
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ISealValidator _preMergeSealValidator;

    public MergeSealValidator(
        IPoSSwitcher poSSwitcher,
        ISealValidator preMergeSealValidator
    )
    {
        _poSSwitcher = poSSwitcher;
        _preMergeSealValidator = preMergeSealValidator;
    }
    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle) =>
        _poSSwitcher.IsPostMerge(header) || _preMergeSealValidator.ValidateParams(parent, header, isUncle);

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        (bool isTerminal, bool isPostMerge) = _poSSwitcher.GetBlockConsensusInfo(header);
        return isPostMerge || _preMergeSealValidator.ValidateSeal(header, force || isTerminal);
    }
}
