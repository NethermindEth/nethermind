// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Trace;

public class MergeRpcRewardCalculator(IRewardCalculator beforeTheMerge, IPoSSwitcher poSSwitcher) : IRewardCalculator
{
    private readonly IRewardCalculator _beforeTheMerge = beforeTheMerge;
    private readonly IPoSSwitcher _poSSwitcher = poSSwitcher;

    public BlockReward[] CalculateRewards(Block block)
    {
        if (_poSSwitcher.IsPostMerge(block.Header))
        {
            return new[] { new BlockReward(block.Beneficiary!, UInt256.Zero) };
        }

        return _beforeTheMerge.CalculateRewards(block);
    }
}
