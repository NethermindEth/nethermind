// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
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
    private InvalidChainTracker.InvalidChainTracker _tracker;

    [SetUp]
    public void Setup()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        BlockCacheService blockCacheService = new();

        _tracker = new(NoPoS.Instance, blockFinder, blockCacheService, new TestLogManager());
    }

    private List<Keccak> MakeChain(int n, bool connectInReverse = false)
    {
        List<Keccak> hashList = new();
        for (int i = 0; i < n; i++)
        {
            Keccak newHash = Keccak.Compute(Random.Shared.NextInt64().ToString());
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
        List<Keccak> hashes = MakeChain(5, connectInReverse);
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
        List<Keccak> hashes = MakeChain(5, connectInReverse);

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
        List<Keccak> mainChain = MakeChain(20, connectInReverse);
        List<Keccak> branchAt5 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt10 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt15 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt15_butConnectOnItem5 = MakeChain(10, connectInReverse);
        List<Keccak> branchAt11_butConnectLater = MakeChain(10, connectInReverse);
        List<Keccak> branchAt5_butConnectLater = MakeChain(10, connectInReverse);

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
        List<Keccak> chain1 = MakeChain(50, connectInReverse);
        List<Keccak> chain2 = MakeChain(50, connectInReverse);
        List<Keccak> chain3 = MakeChain(50, connectInReverse);

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
        List<Keccak> mainChain = MakeChain(50, connectInReverse);
        List<Keccak> secondChain = MakeChain(50, connectInReverse);
        Keccak invalidBlockParent = Keccak.Compute(Random.Shared.NextInt64().ToString());
        Keccak invalidBlock = Keccak.Compute(Random.Shared.NextInt64().ToString());

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

        Keccak invalidBlock = Keccak.Compute("A");
        BlockHeader parentBlockHeader = new BlockHeaderBuilder().TestObject;

        blockCacheService.BlockCache[parentBlockHeader.GetOrCalculateHash()] = new Block(parentBlockHeader);

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

        blockCacheService.BlockCache[blockHeader.GetOrCalculateHash()] = new Block(blockHeader);
        blockCacheService.BlockCache[parentBlockHeader.GetOrCalculateHash()] = new Block(parentBlockHeader);

        IPoSSwitcher alwaysPos = Substitute.For<IPoSSwitcher>();
        alwaysPos.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);

        _tracker = new(alwaysPos, blockFinder, blockCacheService, new TestLogManager()); // Small max section size, to make sure things propagate correctly
        _tracker.OnInvalidBlock(blockHeader.GetOrCalculateHash(), null);

        AssertInvalid(blockHeader.GetOrCalculateHash(), parentBlockHeader.Hash);
    }

    private void AssertValid(Keccak hash)
    {
        Keccak? lastValidHash;
        _tracker.IsOnKnownInvalidChain(hash, out lastValidHash).Should().BeFalse();
    }

    private void AssertInvalid(Keccak hash, Keccak? expectedLsatValidHash = null)
    {
        Keccak? lastValidHash;
        _tracker.IsOnKnownInvalidChain(hash, out lastValidHash).Should().BeTrue();
        if (expectedLsatValidHash is not null)
        {
            lastValidHash.Should().BeEquivalentTo(expectedLsatValidHash);
        }
    }
}
