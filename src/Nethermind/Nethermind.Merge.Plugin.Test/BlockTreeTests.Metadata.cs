// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
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
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertMetadata(0, 9, BlockMetadata.None);
    }

    [Test]
    public void Should_fill_beacon_block_metadata_when_not_moved_to_main_chain()
    {
        BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(4, 10, false)
            .InsertBeaconPivot(7)
            .InsertBeaconHeaders(4, 6)
            .InsertBeaconBlocks(8, 9)
            .SuggestBlocksUsingChainLevels()
            .AssertMetadata(0, 9, BlockMetadata.None);
    }

    [Test]
    public void Removing_beacon_metadata()
    {
        BlockMetadata metadata = BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader;
        metadata = metadata & ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.That(metadata, Is.EqualTo(BlockMetadata.None));

        BlockMetadata metadata2 = BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader | BlockMetadata.Finalized | BlockMetadata.Invalid;
        metadata2 = metadata2 & ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.That(metadata2, Is.EqualTo(BlockMetadata.Finalized | BlockMetadata.Invalid));

        BlockMetadata metadata3 = BlockMetadata.None;
        metadata3 &= ~(BlockMetadata.BeaconBody | BlockMetadata.BeaconHeader);
        Assert.That(metadata3, Is.EqualTo(BlockMetadata.None));

        BlockMetadata metadata4 = BlockMetadata.BeaconHeader;
        metadata4 |= BlockMetadata.BeaconMainChain;
        Assert.That(metadata4, Is.EqualTo(BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain));
    }
}
