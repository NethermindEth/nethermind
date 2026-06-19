// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class InvalidChainTrackerTest
{
    private InvalidChainTracker.InvalidChainTracker _tracker = null!;

    [SetUp]
    public void Setup()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        BlockCacheService blockCacheService = new();

        _tracker = new(NoPoS.Instance, blockFinder, blockCacheService, new TestLogManager());
    }

    private List<Hash256> MakeChain(int n, bool connectInReverse = false)
    {
        List<Hash256> hashList = [];
        for (int i = 0; i < n; i++)
        {
            Hash256 newHash = Keccak.Compute(Random.Shared.NextInt64().ToString());
            hashList.Add(newHash);
        }

        if (connectInReverse)
        {
            for (int i = hashList.Count - 2; i >= 0; i--)
            {
                _tracker.SetChildParent(hashList[i + 1], hashList[i]);
            }
        }
        else
        {
            for (int i = 0; i < hashList.Count - 1; i++)
            {
                _tracker.SetChildParent(hashList[i + 1], hashList[i]);
            }
        }

        return hashList;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void given_aChainOfLength5_when_originBlockIsInvalid_then_otherBlockIsInvalid(bool connectInReverse)
    {
        List<Hash256> hashes = MakeChain(5, connectInReverse);
        AssertValid(hashes[1]);
        AssertValid(hashes[2]);
        AssertValid(hashes[3]);
        AssertValid(hashes[4]);

        _tracker.OnInvalidBlock(hashes[2], hashes[1]);
        AssertValid(hashes[1]);
        AssertInvalid(hashes[2]);
        AssertInvalid(hashes[3]);
        AssertInvalid(hashes[4], hashes[1]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void given_aChainOfLength5_when_aLastValidHashIsInvalidated_then_lastValidHashShouldBeForwarded(bool connectInReverse)
    {
        List<Hash256> hashes = MakeChain(5, connectInReverse);

        _tracker.OnInvalidBlock(hashes[3], hashes[2]);
        AssertInvalid(hashes[3], hashes[2]);

        _tracker.OnInvalidBlock(hashes[2], hashes[1]);
        AssertInvalid(hashes[2], hashes[1]);

        // It should return 1 instead of 2 now
        AssertInvalid(hashes[3], hashes[1]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void given_aTreeWith3Branch_trackerShouldDetectCorrectValidChain(bool connectInReverse)
    {
        List<Hash256> mainChain = MakeChain(20, connectInReverse);
        List<Hash256> branchAt5 = MakeChain(10, connectInReverse);
        List<Hash256> branchAt10 = MakeChain(10, connectInReverse);
        List<Hash256> branchAt15 = MakeChain(10, connectInReverse);
        List<Hash256> branchAt15_butConnectOnItem5 = MakeChain(10, connectInReverse);
        List<Hash256> branchAt11_butConnectLater = MakeChain(10, connectInReverse);
        List<Hash256> branchAt5_butConnectLater = MakeChain(10, connectInReverse);

        _tracker.SetChildParent(mainChain[1], mainChain[0]);
        _tracker.SetChildParent(mainChain[1], mainChain[0]);

        _tracker.SetChildParent(branchAt5[0], mainChain[5]);
        _tracker.SetChildParent(branchAt10[0], mainChain[10]);
        _tracker.SetChildParent(branchAt15[0], mainChain[15]);
        _tracker.SetChildParent(branchAt15_butConnectOnItem5[5], mainChain[15]);

        _tracker.OnInvalidBlock(mainChain[10], mainChain[9]);

        _tracker.SetChildParent(branchAt11_butConnectLater[0], mainChain[11]);
        _tracker.SetChildParent(branchAt5_butConnectLater[0], mainChain[5]);

        AssertValid(branchAt5[5]);

        AssertInvalid(branchAt10[0], mainChain[9]);
        AssertInvalid(branchAt10[5], mainChain[9]);

        AssertInvalid(branchAt15[5], mainChain[9]);
        AssertInvalid(branchAt15[9], mainChain[9]);

        AssertInvalid(branchAt15_butConnectOnItem5[9], mainChain[9]);
        AssertInvalid(branchAt15_butConnectOnItem5[5], mainChain[9]);

        AssertValid(branchAt15_butConnectOnItem5[4]);
        AssertInvalid(branchAt11_butConnectLater[0], mainChain[9]);
        AssertInvalid(branchAt11_butConnectLater[9], mainChain[9]);

        AssertValid(branchAt5_butConnectLater[9]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void whenCreatingACycle_itShouldNotThrow_whenSettingInvalidation(bool connectInReverse)
    {
        List<Hash256> chain1 = MakeChain(50, connectInReverse);
        List<Hash256> chain2 = MakeChain(50, connectInReverse);
        List<Hash256> chain3 = MakeChain(50, connectInReverse);

        _tracker.SetChildParent(chain2[0], chain1[5]);
        _tracker.SetChildParent(chain3[0], chain2[5]);
        _tracker.SetChildParent(chain1[0], chain2[40]);

        _tracker.OnInvalidBlock(chain2[40], Keccak.Zero);
        AssertInvalid(chain1[3]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void givenAnInvalidBlock_whenAttachingLater_trackingShouldStillBeCorrect(bool connectInReverse)
    {
        List<Hash256> mainChain = MakeChain(50, connectInReverse);
        List<Hash256> secondChain = MakeChain(50, connectInReverse);
        Hash256 invalidBlockParent = Keccak.Compute(Random.Shared.NextInt64().ToString());
        Hash256 invalidBlock = Keccak.Compute(Random.Shared.NextInt64().ToString());

        _tracker.OnInvalidBlock(invalidBlock, invalidBlockParent);
        AssertInvalid(invalidBlock);

        AssertValid(mainChain[40]);
        AssertValid(secondChain[40]);

        _tracker.SetChildParent(mainChain[0], invalidBlock);
        _tracker.SetChildParent(secondChain[0], invalidBlock);

        AssertInvalid(mainChain[40]);
        AssertInvalid(secondChain[40]);
    }

    [Test]
    public void givenAnInvalidBlock_ifParentIsNotPostMerge_thenLastValidHashShouldBeZero()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IBlockCacheService blockCacheService = new BlockCacheService();

        Hash256 invalidBlock = Keccak.Compute("A");
        BlockHeader parentBlockHeader = new BlockHeaderBuilder().TestObject;

        blockCacheService.TryAddBlock(new Block(parentBlockHeader));

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(parentBlockHeader).Returns(false);

        _tracker = new(poSSwitcher, blockFinder, blockCacheService, new TestLogManager()); // Small max section size, to make sure things propagate correctly
        _tracker.OnInvalidBlock(invalidBlock, parentBlockHeader.Hash);

        AssertInvalid(invalidBlock, Keccak.Zero);
    }

    [Test]
    public void givenAnInvalidBlock_WithUnknownParent_thenGetParentFromCache()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IBlockCacheService blockCacheService = new BlockCacheService();

        BlockHeader parentBlockHeader = new BlockHeaderBuilder()
            .TestObject;
        BlockHeader blockHeader = new BlockHeaderBuilder()
            .WithParentHash(parentBlockHeader.GetOrCalculateHash()).TestObject;

        blockCacheService.TryAddBlock(new Block(blockHeader));
        blockCacheService.TryAddBlock(new Block(parentBlockHeader));

        IPoSSwitcher alwaysPos = Substitute.For<IPoSSwitcher>();
        alwaysPos.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);

        _tracker = new(alwaysPos, blockFinder, blockCacheService, new TestLogManager()); // Small max section size, to make sure things propagate correctly
        _tracker.OnInvalidBlock(blockHeader.GetOrCalculateHash(), null);

        AssertInvalid(blockHeader.GetOrCalculateHash(), parentBlockHeader.Hash);
    }

    [Test]
    public async Task GetNode_WhenCalledConcurrently_DoesNotLoseChildren()
    {
        // Regression test: before the fix, two threads could both see TryGet return false
        // for the same parent hash, create separate Node objects, and the second Set() would
        // overwrite the first. The thread holding the evicted Node's children were then
        // invisible to OnInvalidBlock's PropagateLastValidHash, so descendants were never
        // marked invalid — causing engine_newPayload to return SYNCING instead of INVALID.
        Hash256 parent = Keccak.Compute("parent");
        Hash256 grandparent = Keccak.Compute("grandparent");

        const int threadCount = 32;
        Hash256[] children = new Hash256[threadCount];
        for (int i = 0; i < threadCount; i++)
            children[i] = Keccak.Compute($"child{i}");

        // Simulate many concurrent SetChildParent calls on the same parent hash,
        // as happens when the beacon header sync feed and engine API calls race.
        await Parallel.ForEachAsync(children, async (child, _) =>
        {
            await Task.Yield();
            _tracker.SetChildParent(child, parent);
        });

        _tracker.OnInvalidBlock(parent, grandparent);

        foreach (Hash256 child in children)
        {
            AssertInvalid(child, grandparent);
        }
    }

    private void AssertValid(Hash256 hash) =>
        Assert.That(_tracker.IsOnKnownInvalidChain(hash, out _), Is.False);

    private void AssertInvalid(Hash256 hash, Hash256? expectedLsatValidHash = null)
    {
        Assert.That(_tracker.IsOnKnownInvalidChain(hash, out Hash256? lastValidHash), Is.True);
        if (expectedLsatValidHash is not null)
        {
            Assert.That(lastValidHash, Is.EqualTo(expectedLsatValidHash));
        }
    }
}
