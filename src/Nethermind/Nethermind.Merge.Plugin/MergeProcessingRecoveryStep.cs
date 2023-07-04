// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public class MergeProcessingRecoveryStep : IBlockPreprocessorStep
{
    private readonly IPoSSwitcher _poSSwitcher;

    public MergeProcessingRecoveryStep(
        IPoSSwitcher poSSwitcher)
    {
        _poSSwitcher = poSSwitcher;
    }

    public void RecoverData(Block block)
    {
        block.Header.IsPostMerge = _poSSwitcher.IsPostMerge(block.Header);

        if (block.Author is null && block.IsPostMerge)
        {
            block.Header.Author = block.Beneficiary;
        }
    }
}
