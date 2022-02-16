//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
