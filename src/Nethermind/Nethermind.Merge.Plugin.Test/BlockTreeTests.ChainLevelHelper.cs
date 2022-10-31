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

using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class BlockTreeTests
{
    [Test]
    public void Can_sync_using_chain_levels()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }

    [Test]
    public void Can_sync_using_chain_levels_with_restart()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .Restart()
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }

    [Test]
    public void Correct_levels_after_chain_level_sync()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9)
            .AssertNotForceNewBeaconSync();
    }

    [Test]
    public void Correct_levels_after_chain_level_sync_with_nullable_td()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null)
            .InsertBeaconBlocks(8, 9, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Correct_levels_after_chain_level_sync_with_zero_td()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Zero)
            .InsertBeaconBlocks(8, 9, BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Zero)
            .SuggestBlocksUsingChainLevels()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9, 10000000)
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Correct_levels_with_chain_fork()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .InsertFork(1, 9)
            .AssertBestSuggestedBody(3)
            .SuggestBlocksUsingChainLevels()
            .AssertBestSuggestedBody(9)
            .AssertChainLevel(0, 9);
    }

    [Test]
    public void Correct_levels_after_chain_level_sync_with_disconnected_beacon_chain()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 15)
            .InsertBeaconPivot(11)
            .SetProcessDestination(11)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconHeaders(8, 10)
            .SuggestBlocksUsingChainLevels(20)
            .AssertChainLevel(0, 4)
            .AssertForceNewBeaconSync();
    }
}
