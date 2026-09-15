// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class BlockCacheServiceTest
{
    [Test]
    public void prunes_highest_unprotected_block_and_returns_false_when_added_block_is_pruned()
    {
        BlockCacheService blockCacheService = new(2);
        Block block1 = Build.A.Block.WithNumber(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).TestObject;
        Block block3 = Build.A.Block.WithNumber(3).TestObject;
        Hash256 block1Hash = block1.GetOrCalculateHash();
        Hash256 block2Hash = block2.GetOrCalculateHash();
        Hash256 block3Hash = block3.GetOrCalculateHash();

        Assert.That(blockCacheService.TryAddBlock(block1), Is.True);
        Assert.That(blockCacheService.TryAddBlock(block2), Is.True);
        Assert.That(blockCacheService.TryAddBlock(block3), Is.False);

        Assert.That(blockCacheService.BlockCache, Has.Count.EqualTo(2));
        Assert.That(blockCacheService.BlockCache.ContainsKey(block1Hash), Is.True);
        Assert.That(blockCacheService.BlockCache.ContainsKey(block2Hash), Is.True);
        Assert.That(blockCacheService.BlockCache.ContainsKey(block3Hash), Is.False);
    }

    [Test]
    public void preserves_head_and_finalized_blocks_when_pruning()
    {
        Block finalizedBlock = Build.A.Block.WithNumber(1).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).TestObject;
        Block headBlock = Build.A.Block.WithNumber(3).TestObject;
        Hash256 finalizedHash = finalizedBlock.GetOrCalculateHash();
        Hash256 block2Hash = block2.GetOrCalculateHash();
        Hash256 headHash = headBlock.GetOrCalculateHash();
        BlockCacheService blockCacheService = new(2, ProtectingStrategy(finalizedHash, headHash));

        Assert.That(blockCacheService.TryAddBlock(finalizedBlock), Is.True);
        Assert.That(blockCacheService.TryAddBlock(block2), Is.True);
        Assert.That(blockCacheService.TryAddBlock(headBlock), Is.True);

        Assert.That(blockCacheService.BlockCache, Has.Count.EqualTo(2));
        Assert.That(blockCacheService.BlockCache.ContainsKey(finalizedHash), Is.True);
        Assert.That(blockCacheService.BlockCache.ContainsKey(block2Hash), Is.False);
        Assert.That(blockCacheService.BlockCache.ContainsKey(headHash), Is.True);
    }

    [Test]
    public void preserves_protected_blocks_when_all_candidates_are_protected()
    {
        Block finalizedBlock = Build.A.Block.WithNumber(1).TestObject;
        Block headBlock = Build.A.Block.WithNumber(2).TestObject;
        Hash256 finalizedHash = finalizedBlock.GetOrCalculateHash();
        Hash256 headHash = headBlock.GetOrCalculateHash();
        BlockCacheService blockCacheService = new(1, ProtectingStrategy(finalizedHash, headHash));

        Assert.That(blockCacheService.TryAddBlock(finalizedBlock), Is.True);
        Assert.That(blockCacheService.TryAddBlock(headBlock), Is.True);

        Assert.That(blockCacheService.BlockCache, Has.Count.EqualTo(2));
        Assert.That(blockCacheService.BlockCache.ContainsKey(finalizedHash), Is.True);
        Assert.That(blockCacheService.BlockCache.ContainsKey(headHash), Is.True);
    }

    [Test]
    public void clears_cache_when_beacon_sync_stops()
    {
        IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
        BlockCacheService blockCacheService = new(4, beaconSyncStrategy);
        blockCacheService.TryAddBlock(Build.A.Block.WithNumber(1).TestObject);
        blockCacheService.TryAddBlock(Build.A.Block.WithNumber(2).TestObject);
        Assert.That(blockCacheService.BlockCache, Has.Count.EqualTo(2));

        beaconSyncStrategy.BeaconSyncStopped += Raise.Event<Action>();

        Assert.That(blockCacheService.BlockCache, Is.Empty);
    }

    private static IBeaconSyncStrategy ProtectingStrategy(Hash256 finalizedHash, Hash256 headHash)
    {
        IBeaconSyncStrategy beaconSyncStrategy = Substitute.For<IBeaconSyncStrategy>();
        beaconSyncStrategy.GetFinalizedHash().Returns(finalizedHash);
        beaconSyncStrategy.GetHeadBlockHash().Returns(headHash);
        return beaconSyncStrategy;
    }
}
