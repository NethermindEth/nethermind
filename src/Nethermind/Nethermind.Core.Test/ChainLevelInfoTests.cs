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
    public void Reinserting_block_info_with_keep_existing_metadata_merges_metadata(BlockMetadata existingMetadata, BlockMetadata newMetadata)
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, existingMetadata));

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0, newMetadata), setAsMain: false, keepExistingMetadata: true);

        Assert.That(level.BlockInfos, Has.Length.EqualTo(1));
        Assert.That(level.BlockInfos[0].Metadata, Is.EqualTo(existingMetadata | newMetadata));
    }

    [Test]
    public void Reinserting_non_beacon_block_info_clears_beacon_metadata_by_default()
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain));

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0), setAsMain: false);

        Assert.That(level.BlockInfos[0].Metadata, Is.EqualTo(BlockMetadata.None));
    }

    [TestCase(
        BlockMetadata.BeaconHeader,
        BlockMetadata.BeaconBody,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody,
        TestName = "Keeps stored header metadata on body-only re-insert")]
    [TestCase(
        BlockMetadata.BeaconBody,
        BlockMetadata.BeaconHeader,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody,
        TestName = "Keeps stored body metadata on header-only re-insert")]
    [TestCase(
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain,
        BlockMetadata.BeaconBody,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain,
        TestName = "Keeps stored main-chain metadata on body-only re-insert")]
    [TestCase(
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain,
        TestName = "Keeps stored body metadata on partial header re-insert")]
    [TestCase(
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain,
        BlockMetadata.BeaconBody,
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain,
        TestName = "Keeps all stored beacon metadata on body-only re-insert")]
    public void Reinserting_beacon_block_info_keeps_sticky_beacon_flags_by_default(
        BlockMetadata existingMetadata,
        BlockMetadata newMetadata,
        BlockMetadata expectedMetadata)
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, existingMetadata));

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0, newMetadata), setAsMain: false);

        Assert.That(level.BlockInfos[0].Metadata, Is.EqualTo(expectedMetadata));
    }

    [TestCase(
        BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain,
        BlockMetadata.BeaconBody,
        TestName = "Keeps processed flag when body metadata expands stored beacon metadata")]
    [TestCase(
        BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain,
        BlockMetadata.BeaconHeader,
        TestName = "Keeps processed flag when header metadata expands stored beacon metadata")]
    public void Reinserting_beacon_block_info_keeps_processed_flag_when_metadata_expands(
        BlockMetadata existingMetadata,
        BlockMetadata newMetadata)
    {
        BlockMetadata allBeaconMetadata = BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain;
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, existingMetadata) { WasProcessed = true });

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0, newMetadata), setAsMain: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(level.BlockInfos[0].Metadata, Is.EqualTo(allBeaconMetadata), "metadata");
            Assert.That(level.BlockInfos[0].WasProcessed, Is.True, "processed");
        }
    }

    [TestCase(true, false, TestName = "Does not keep processed flag when non-beacon metadata changes")]
    [TestCase(false, true, TestName = "Does not keep processed flag when total difficulty changes")]
    public void Reinserting_beacon_block_info_does_not_keep_processed_flag_when_stable_fields_change(
        bool changesNonBeaconMetadata,
        bool changesTotalDifficulty)
    {
        BlockInfo incomingBlockInfo = new(_hash, 0, BlockMetadata.BeaconBody);
        if (changesNonBeaconMetadata)
        {
            incomingBlockInfo.Metadata |= BlockMetadata.Finalized;
        }

        if (changesTotalDifficulty)
        {
            incomingBlockInfo.TotalDifficulty = 1;
        }

        ChainLevelInfo level = new(false,
            new BlockInfo(_hash, 0, BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain) { WasProcessed = true });

        level.InsertBlockInfo(_hash, incomingBlockInfo, setAsMain: false);

        Assert.That(level.BlockInfos[0].WasProcessed, Is.False);
    }

    [TestCase(5UL, 0UL, TestName = "Keeps processed flag when cached block number differs from incoming block number")]
    [TestCase(0UL, 5UL, TestName = "Keeps processed flag when incoming block number differs from cached block number")]
    public void Reinserting_beacon_block_info_keeps_processed_flag_when_block_number_differs(
        ulong existingBlockNumber,
        ulong incomingBlockNumber)
    {
        BlockInfo existingBlockInfo = new(_hash, 0, BlockMetadata.BeaconHeader | BlockMetadata.BeaconMainChain)
        {
            BlockNumber = existingBlockNumber,
            WasProcessed = true,
        };
        BlockInfo incomingBlockInfo = new(_hash, 0, BlockMetadata.BeaconBody) { BlockNumber = incomingBlockNumber };
        ChainLevelInfo level = new(false, existingBlockInfo);

        level.InsertBlockInfo(_hash, incomingBlockInfo, setAsMain: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(level.BlockInfos[0].Metadata,
                Is.EqualTo(BlockMetadata.BeaconHeader | BlockMetadata.BeaconBody | BlockMetadata.BeaconMainChain), "metadata");
            Assert.That(level.BlockInfos[0].WasProcessed, Is.True, "processed");
        }
    }

    [Test]
    public void Reinserting_block_info_keeps_processed_flag()
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0, BlockMetadata.BeaconHeader) { WasProcessed = true });

        level.InsertBlockInfo(_hash, new BlockInfo(_hash, 0), setAsMain: false, keepExistingMetadata: true);

        Assert.That(level.BlockInfos[0].WasProcessed, Is.True);
    }

    [Test]
    public void Inserting_new_block_info_appends_to_level()
    {
        ChainLevelInfo level = new(false, new BlockInfo(_hash, 0));

        level.InsertBlockInfo(TestItem.KeccakB, new BlockInfo(TestItem.KeccakB, 0, BlockMetadata.BeaconHeader), setAsMain: false, keepExistingMetadata: true);

        Assert.That(level.BlockInfos, Has.Length.EqualTo(2));
        Assert.That(level.FindBlockInfo(TestItem.KeccakB)!.Metadata, Is.EqualTo(BlockMetadata.BeaconHeader));
    }
}
