// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
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
        // Every hash resolves to an executed post-merge header, so a parent qualifies as the last valid hash.
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(static call => Build.A.BlockHeader.WithHash(call.Arg<Hash256>()).TestObject);
        blockTree.GetInfo(Arg.Any<ulong>(), Arg.Any<Hash256>())
            .Returns(static call => (new BlockInfo(call.Arg<Hash256>(), 0) { WasProcessed = true }, null));
        BlockCacheService blockCacheService = new();

        _tracker = new(AlwaysPoS.Instance, blockTree, blockCacheService, new TestLogManager());
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

    [Test]
    public void given_aChainOfLength5_when_originBlockIsInvalid_then_otherBlockIsInvalid([Values] bool connectInReverse)
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

    [Test]
    public void given_aChainOfLength5_when_aLastValidHashIsInvalidated_then_lastValidHashShouldBeForwarded([Values] bool connectInReverse)
    {
        List<Hash256> hashes = MakeChain(5, connectInReverse);

        _tracker.OnInvalidBlock(hashes[3], hashes[2]);
        AssertInvalid(hashes[3], hashes[2]);

        _tracker.OnInvalidBlock(hashes[2], hashes[1]);
        AssertInvalid(hashes[2], hashes[1]);

        // It should return 1 instead of 2 now
        AssertInvalid(hashes[3], hashes[1]);
    }

    [Test]
    public void given_aTreeWith3Branch_trackerShouldDetectCorrectValidChain([Values] bool connectInReverse)
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

    [Test]
    public void whenCreatingACycle_itShouldNotThrow_whenSettingInvalidation([Values] bool connectInReverse)
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

    [Test]
    public void givenAnInvalidBlock_whenAttachingLater_trackingShouldStillBeCorrect([Values] bool connectInReverse)
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
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockCacheService blockCacheService = new BlockCacheService();

        Hash256 invalidBlock = Keccak.Compute("A");
        BlockHeader parentBlockHeader = new BlockHeaderBuilder().TestObject;

        blockCacheService.TryAddBlock(new Block(parentBlockHeader));

        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(parentBlockHeader).Returns(false);

        _tracker = new(poSSwitcher, blockTree, blockCacheService, new TestLogManager()); // Small max section size, to make sure things propagate correctly
        _tracker.OnInvalidBlock(invalidBlock, parentBlockHeader.Hash);

        AssertInvalid(invalidBlock, Keccak.Zero);
    }

    [Test]
    public void givenAnInvalidBlock_WithUnknownParent_thenGetParentFromCache()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBlockCacheService blockCacheService = new BlockCacheService();

        BlockHeader parentBlockHeader = new BlockHeaderBuilder()
            .TestObject;
        BlockHeader blockHeader = new BlockHeaderBuilder()
            .WithParentHash(parentBlockHeader.GetOrCalculateHash()).TestObject;

        blockCacheService.TryAddBlock(new Block(blockHeader));
        blockCacheService.TryAddBlock(new Block(parentBlockHeader));
        blockTree.GetInfo(parentBlockHeader.Number, parentBlockHeader.Hash!)
            .Returns((new BlockInfo(parentBlockHeader.Hash!, 0) { WasProcessed = true }, null));

        IPoSSwitcher alwaysPos = Substitute.For<IPoSSwitcher>();
        alwaysPos.IsPostMerge(Arg.Any<BlockHeader>()).Returns(true);

        _tracker = new(alwaysPos, blockTree, blockCacheService, new TestLogManager()); // Small max section size, to make sure things propagate correctly
        _tracker.OnInvalidBlock(blockHeader.GetOrCalculateHash(), null);

        AssertInvalid(blockHeader.GetOrCalculateHash(), parentBlockHeader.Hash);
    }

    // paris.md: the last valid hash names an ancestor that is fully validated, or is null when none can be determined.
    [Test]
    public void givenAnInvalidBlock_whenParentWasNotExecuted_thenLastValidHashIsUnknown([Values] bool parentKnown)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader parentBlockHeader = new BlockHeaderBuilder().TestObject;
        if (parentKnown)
        {
            blockTree.FindHeader(parentBlockHeader.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
                .Returns(parentBlockHeader);
            blockTree.GetInfo(parentBlockHeader.Number, parentBlockHeader.Hash!)
                .Returns((new BlockInfo(parentBlockHeader.Hash!, 0), null));
        }

        _tracker = new(AlwaysPoS.Instance, blockTree, new BlockCacheService(), new TestLogManager());
        Hash256 invalidBlock = Keccak.Compute("A");
        Hash256 child = Keccak.Compute("B");
        _tracker.OnInvalidBlock(invalidBlock, parentBlockHeader.Hash);
        _tracker.SetChildParent(child, invalidBlock);

        AssertInvalidWithUnknownLastValidHash(invalidBlock);
        AssertInvalidWithUnknownLastValidHash(child);
    }

    [Test]
    public void givenAnInvalidBlock_whenParentIsAlreadyInvalid_thenLastValidHashIsTheParents()
    {
        Hash256 lastValid = Keccak.Compute("A");
        Hash256 parent = Keccak.Compute("B");
        Hash256 invalidBlock = Keccak.Compute("C");

        _tracker.OnInvalidBlock(parent, lastValid);
        _tracker.OnInvalidBlock(invalidBlock, parent);

        AssertInvalid(invalidBlock, lastValid);
    }

    [Test]
    public void processed_parent_is_only_known_when_validated_before_disposal(
        [Values] bool wasValidated, [Values] bool disposed, [Values] bool invalidBeforeProcessing)
    {
        IBranchProcessor branchProcessor = SetupProcessingInterceptor();
        if (disposed) _tracker.Dispose();
        Block parent = Build.A.Block.TestObject;

        if (invalidBeforeProcessing)
        {
            RecordInvalidChain();
            AssertInvalidWithUnknownLastValidHash(TestItem.KeccakA);
            AssertInvalidWithUnknownLastValidHash(TestItem.KeccakB);
        }
        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = wasValidated });
        if (!invalidBeforeProcessing) RecordInvalidChain();

        bool invalid = _tracker.IsOnKnownInvalidChain(TestItem.KeccakA, out Hash256? lastValidHash);
        bool descendantInvalid = _tracker.IsOnKnownInvalidChain(TestItem.KeccakB, out Hash256? descendantHash);
        bool siblingInvalid = _tracker.IsOnKnownInvalidChain(TestItem.KeccakC, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(invalid, Is.True);
            Assert.That(lastValidHash, Is.EqualTo(wasValidated && !disposed ? parent.Hash : null));
            Assert.That(descendantInvalid, Is.True);
            Assert.That(descendantHash, Is.EqualTo(lastValidHash));
            Assert.That(siblingInvalid, Is.False);
        }

        void RecordInvalidChain()
        {
            _tracker.OnInvalidBlock(TestItem.KeccakA, parent.Hash);
            _tracker.SetChildParent(TestItem.KeccakB, TestItem.KeccakA);
            _tracker.SetChildParent(TestItem.KeccakC, parent.Hash!);
        }
    }

    [Test]
    public void validated_pow_parent_is_reported_as_zero_without_a_stored_header([Values] bool invalidBeforeProcessing)
    {
        IBranchProcessor branchProcessor = SetupProcessingInterceptor(NoPoS.Instance);
        Block parent = Build.A.Block.TestObject;

        if (invalidBeforeProcessing) _tracker.OnInvalidBlock(TestItem.KeccakA, parent.Hash);
        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });
        if (!invalidBeforeProcessing) _tracker.OnInvalidBlock(TestItem.KeccakA, parent.Hash);

        AssertInvalid(TestItem.KeccakA, Keccak.Zero);
    }

    [Test]
    public void invalid_parent_processing_event_does_not_refresh_unknown_descendants()
    {
        IBranchProcessor branchProcessor = SetupProcessingInterceptor();
        Block parent = Build.A.Block.TestObject;
        _tracker.OnInvalidBlock(parent.Hash!, TestItem.KeccakA);
        _tracker.OnInvalidBlock(TestItem.KeccakB, parent.Hash);

        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });

        AssertInvalidWithUnknownLastValidHash(TestItem.KeccakB);
    }

    [Test]
    public void parent_validated_during_invalid_verdict_resolution_refreshes_the_verdict()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IBranchProcessor branchProcessor = SetupProcessingInterceptor(blockTree: blockTree);
        Block parent = Build.A.Block.TestObject;
        blockTree.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent.Header);
        blockTree.GetInfo(parent.Number, parent.Hash!).Returns(_ =>
        {
            branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });
            return (new BlockInfo(parent.Hash!, 0), null);
        });

        _tracker.OnInvalidBlock(TestItem.KeccakA, parent.Hash);

        AssertInvalid(TestItem.KeccakA, parent.Hash);
    }

    [Test]
    public void tracked_invalid_parent_takes_precedence_over_its_processing_event()
    {
        IBranchProcessor branchProcessor = SetupProcessingInterceptor();
        Block ancestor = Build.A.Block.TestObject;
        Block parent = Build.A.Block.WithParent(ancestor).TestObject;
        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(ancestor, []) { WasValidated = true });
        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });

        _tracker.OnInvalidBlock(parent.Hash!, ancestor.Hash);
        _tracker.OnInvalidBlock(TestItem.KeccakA, parent.Hash);

        AssertInvalid(TestItem.KeccakA, ancestor.Hash);
    }

    [Test]
    public void processing_evidence_is_bounded()
    {
        IBranchProcessor branchProcessor = SetupProcessingInterceptor();
        Block parent = Build.A.Block.WithNumber(0).TestObject;
        Hash256 firstHash = parent.Hash!;
        branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });
        for (int i = 1; i <= 1024; i++)
        {
            parent = Build.A.Block.WithNumber(i).TestObject;
            branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(parent, []) { WasValidated = true });
        }

        _tracker.OnInvalidBlock(TestItem.KeccakA, firstHash);
        _tracker.OnInvalidBlock(TestItem.KeccakB, parent.Hash);

        AssertInvalidWithUnknownLastValidHash(TestItem.KeccakA);
        AssertInvalid(TestItem.KeccakB, parent.Hash);
    }

    private IBranchProcessor SetupProcessingInterceptor(IPoSSwitcher? poSSwitcher = null, IBlockTree? blockTree = null)
    {
        IBranchProcessor branchProcessor = Substitute.For<IBranchProcessor>();
        _tracker = new(poSSwitcher ?? AlwaysPoS.Instance, blockTree ?? Substitute.For<IBlockTree>(), new BlockCacheService(), new TestLogManager());
        _tracker.SetupBlockchainProcessorInterceptor(Substitute.For<IBlockProcessingQueue>(), branchProcessor);
        return branchProcessor;
    }

    [Test]
    public void queryingValidHashes_shouldNotEvictRecordedInvalidChainNode()
    {
        List<Hash256> hashes = MakeChain(3);
        _tracker.OnInvalidBlock(hashes[1], hashes[0]);
        AssertInvalid(hashes[1]);

        for (int i = 0; i < 4096; i++)
        {
            _tracker.IsOnKnownInvalidChain(Keccak.Compute($"valid-{i}"), out _);
        }

        AssertInvalid(hashes[1]);
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

    private void AssertInvalidWithUnknownLastValidHash(Hash256 hash)
    {
        bool isInvalid = _tracker.IsOnKnownInvalidChain(hash, out Hash256? lastValidHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(isInvalid, Is.True);
            Assert.That(lastValidHash, Is.Null);
        }
    }
}
