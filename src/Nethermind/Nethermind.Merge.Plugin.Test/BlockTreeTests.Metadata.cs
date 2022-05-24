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

using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class BlockTreeTests
{
    [Test]
    public void Should_set_correct_metadata()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(4, 6)
            .InsertBeaconBlocks(7, 9)
            .AssertMetadata(0, 4, BlockMetadata.None)
            .AssertMetadata(5, 6, BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain)
            .AssertMetadata(7, 9, BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain);
    }
    
    [Test]
    public void Should_set_correct_metadata_after_suggest_blocks_using_chain_levels()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertHeaders(3, 6)
            .InsertBeaconBlocks(7, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertMetadata(0, 9, BlockMetadata.None);
    }
    
    [Test]
    public void Should_fill_beacon_block_metadata_when_not_moved_to_main_chain()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10, false)
            .InsertBeaconPivot(7)
            .InsertHeaders(3, 6)
            .InsertBeaconBlocks(7, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertMetadata(0, 3, BlockMetadata.None)
            .AssertMetadata(4, 6, BlockMetadata.None)
            .AssertMetadata(7, 9, BlockMetadata.None);
    }
    
    [Test]
    public void Removing_beacon_metadata()
    {
        BlockMetadata metadata = BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader;
        metadata = metadata & ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.AreEqual(BlockMetadata.None, metadata);
        
        BlockMetadata metadata2 = BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.Finalized | BlockMetadata.Invalid;
        metadata2 = metadata2 & ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.AreEqual(BlockMetadata.Finalized | BlockMetadata.Invalid, metadata2);
        
        BlockMetadata metadata3 = BlockMetadata.None;
        metadata3 &= ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.AreEqual(BlockMetadata.None, metadata3);
        
        BlockMetadata metadata4 = BlockMetadata.BeaconHeader;
        metadata4 |= BlockMetadata.BeaconMainChain;
        Assert.AreEqual(BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain, metadata4);
    }
}
