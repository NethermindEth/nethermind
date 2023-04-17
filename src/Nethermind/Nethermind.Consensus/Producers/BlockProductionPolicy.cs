// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Producers;

/*
* This class was introduced because of the merge changes.
* PreMerge starting block production depended on the flag from mining config.
* However, in the post-merge world, our node might not be miner pre-merge, and it is a validator after the merge. Generally, in post-merge, we should always start a block production logic. If we weren't pre-merge miner merge plugin will be able to wrap null as a preMergeBlockProducer.
* To resolve this problem BlockProductionPolicy was introduced.
 */
public class BlockProductionPolicy : IBlockProductionPolicy
{
    private readonly IMiningConfig _miningConfig;

    public BlockProductionPolicy(
        IMiningConfig miningConfig)
    {
        _miningConfig = miningConfig;
    }

    public bool ShouldStartBlockProduction() => _miningConfig.Enabled;
}

public interface IBlockProductionPolicy
{
    bool ShouldStartBlockProduction();
}
