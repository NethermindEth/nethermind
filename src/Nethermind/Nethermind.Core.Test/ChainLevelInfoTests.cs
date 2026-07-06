// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ChainLevelInfoTests
{
    private static readonly Hash256 _hash = TestItem.KeccakA;

    [TestCase(BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain, BlockMetadata.None)]
    [TestCase(BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain, BlockMetadata.None)]
    [TestCase(BlockMetadata.None, BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain)]
    [TestCase(BlockMetadata.BeaconMainChain, BlockMetadata.BeaconHeader)]
    [TestCase(BlockMetadata.Finalized, BlockMetadata.None)]
    public void Reinserting_block_info_merges_metadata(BlockMetadata existingMetadata, BlockMetadata newMetadata)
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, existingMetadata));

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0, newMetadata), setAsMain: false);

        Assert.That(level.BlockInfos, Has.Length.EqualTo(1));
        Assert.That(level.BlockInfos[0].Metadata, Is.EqualTo(existingMetadata | newMetadata));
    }

    [Test]
    public void Reinserting_block_info_keeps_processed_flag()
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, BlockMetadata.BeaconHeader) { WasProcessed = true });

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0), setAsMain: false);

        Assert.That(level.BlockInfos[0].WasProcessed, Is.True);
    }

    [Test]
    public void Inserting_new_block_info_appends_to_level()
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0));

        level.InsertBlockInfo(TestItem.KeccakB, new BlockInfo(TestItem.KeccakB, 0, BlockMetadata.BeaconHeader), setAsMain: false);

        Assert.That(level.BlockInfos, Has.Length.EqualTo(2));
        Assert.That(level.FindBlockInfo(TestItem.KeccakB)!.Metadata, Is.EqualTo(BlockMetadata.BeaconHeader));
    }
}
