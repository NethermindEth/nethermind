// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public class MergeSealValidator(
    IPoSSwitcher poSSwitcher,
    ISealValidator preMergeSealValidator)
    : ISealValidator
{
    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle) =>
        poSSwitcher.IsPostMerge(header) || preMergeSealValidator.ValidateParams(parent, header, isUncle);

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        (bool isTerminal, bool isPostMerge) = poSSwitcher.GetBlockConsensusInfo(header);
        return isPostMerge || preMergeSealValidator.ValidateSeal(header, force || isTerminal);
    }
}
