// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class UnsafeHeadFollowerTests
{
    private IBlockTree _chain = null!;
    private ChainEngine _engine = null!;
    private IEezSequencerApi _sequencer = null!;
    private FollowerHeads _heads = null!;
    private UnsafeHeadFollower _follower = null!;

    [SetUp]
    public async Task SetUp()
    {
        _chain = Build.A.BlockTree().OfChainLength(3).TestObject;
        _engine = new ChainEngine(_chain);
        _sequencer = Substitute.For<IEezSequencerApi>();
        _sequencer.GetBlockByNumber(Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            BlockHeader? header = _chain.FindHeader(call.ArgAt<ulong>(0));
            return header is null ? (EezL1Block?)null : new EezL1Block { Number = header.Number, Hash = header.Hash! };
        });
        _heads = new FollowerHeads(_engine, _chain, _chain.Genesis!);
        await _heads.AdvanceSafe(_chain.FindHeader(2UL)!);
        _engine.Forkchoices.Clear();
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance);
    }

    [Test]
    public async Task Advance_SequencerAheadOfSafe_InsertsItsBlocksInOrder()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 2, extraData: 0xaa));

        await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_engine.Inserted.Select(static b => b.Number), Is.EqualTo(new ulong[] { 3, 4 }), "the missing blocks enter the chain oldest first");
            Assert.That(_chain.Head!.Hash, Is.EqualTo(ahead[^1].Hash), "the head is the sequencer's");
            Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[^1].Hash), "the follower's heads record the new head");
            Assert.That(_engine.Forkchoices[^1].Safe, Is.EqualTo(_heads.Safe.Hash), "the sequencer never moves safe");
        }
    }

    [Test]
    public async Task Advance_BranchBelowSafe_IsIgnored()
    {
        Serve(Branch(_chain.FindHeader(1UL)!, 3, extraData: 0xbb));

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_engine.Inserted, Is.Empty, "a sequencer cannot move the chain off the safe head");
    }

    [Test]
    public async Task Advance_SameHeadAgain_DoesNothing()
    {
        Serve(Branch(_heads.Safe, 1, extraData: 0xcc));
        await _follower.Advance(_heads, CancellationToken.None);
        _engine.Inserted.Clear();

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_engine.Inserted, Is.Empty, "the sequencer head that is already the head is not fetched again");
    }

    [Test]
    public async Task Advance_InvalidSequencerBlock_KeepsFollowing()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 1, extraData: 0xdd));
        _engine.Rejected.Add(ahead[0].Hash!);

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_chain.Head!.Number, Is.EqualTo(2), "a block the node rejects is the sequencer's fault and does not stop the follower");
    }

    [Test]
    public async Task Advance_BlockWhoseHashDiffers_IsIgnored()
    {
        Block[] ahead = Branch(_heads.Safe, 1, extraData: 0xee);
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Hash = Keccak.Compute("claimed"), Number = ahead[0].Number });
        _sequencer.GetRawBlock(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(Rlp.Encode(ahead[0]).Bytes);

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_engine.Inserted, Is.Empty, "a block that is not the one the sequencer named is refused");
    }

    [Test]
    public async Task Advance_BlockDerivationWouldNotBuild_IsIgnored()
    {
        Block forged = Build.A.Block.WithParent(_heads.Safe).WithMixHash(Keccak.Compute("randao")).WithExtraData([0xef]).TestObject;
        Serve([forged]);

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_engine.Inserted, Is.Empty, "a sequencer block with a header L1 derivation would not rebuild never enters the chain");
    }

    /// <summary>
    /// The head was reset below blocks the sequencer still builds on, as after an L1 reorganization:
    /// 1. the node holds the sequencer's blocks off the canonical chain;
    /// 2. they are taken locally and become the head again.
    /// </summary>
    [Test]
    public async Task Advance_AfterAHeadReset_RejoinsTheBlocksTheNodeHolds()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 2, extraData: 0xa1));
        await _follower.Advance(_heads, CancellationToken.None);
        await _heads.Reset(_heads.Safe);
        _sequencer.ClearReceivedCalls();

        await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_chain.Head!.Hash, Is.EqualTo(ahead[^1].Hash), "the sequencer's head is the head again");
            Assert.That(RawBlockReads(), Is.Zero, "blocks the node already holds are not fetched again");
        }
    }

    [Test]
    public async Task Advance_LargeUnsettledRange_CatchesUpInBoundedSteps()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, UnsafeHeadFollower.MaxDepth + 1, extraData: 0xb1));
        Hash256 safe = _heads.Safe.Hash!, finalized = _heads.Finalized.Hash!;

        await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_engine.Inserted, Has.Count.EqualTo(UnsafeHeadFollower.MaxBlocksPerAdvance));
            Assert.That(RawBlockReads(), Is.EqualTo(UnsafeHeadFollower.MaxBlocksPerAdvance));
        }
        while (_heads.Head.Number < ahead[^1].Number)
            await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[^1].Hash));
            Assert.That(_heads.Safe.Hash, Is.EqualTo(safe));
            Assert.That(_heads.Finalized.Hash, Is.EqualTo(finalized));
        }
    }

    [Test]
    public async Task Advance_UnsafeReorg_ReplacesOnlyTheUnsettledBranch()
    {
        Block[] first = Serve(Branch(_heads.Safe, 2 * UnsafeHeadFollower.MaxBlocksPerAdvance, extraData: 0xb2));
        while (_heads.Head.Hash != first[^1].Hash)
            await _follower.Advance(_heads, CancellationToken.None);
        Block[] replacement = Serve(Branch(_heads.Safe, UnsafeHeadFollower.MaxBlocksPerAdvance + 1, extraData: 0xb3));
        Hash256 safe = _heads.Safe.Hash!, finalized = _heads.Finalized.Hash!;

        while (_heads.Head.Hash != replacement[^1].Hash)
            await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_chain.Head!.Hash, Is.EqualTo(replacement[^1].Hash));
            Assert.That(_heads.Safe.Hash, Is.EqualTo(safe));
            Assert.That(_heads.Finalized.Hash, Is.EqualTo(finalized));
        }
    }

    [Test]
    public async Task Advance_SequencerReturnsToSafe_DropsOnlyUnsafeBlocks()
    {
        Serve(Branch(_heads.Safe, 2, extraData: 0xb4));
        await _follower.Advance(_heads, CancellationToken.None);
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = _heads.Safe.Number, Hash = _heads.Safe.Hash! });

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_heads.Head.Hash, Is.EqualTo(_heads.Safe.Hash));
    }

    [Test]
    public async Task Advance_HistoryParentReorganized_ReturnsToSafeBeforeExecution()
    {
        Serve(Branch(_heads.Safe, 1, extraData: 0xb5));
        IUnsafeBlockPreparation preparation = Substitute.For<IUnsafeBlockPreparation>();
        preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.ParentReorganized));
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance, preparation);

        await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_engine.Inserted, Is.Empty);
            Assert.That(_heads.Head.Hash, Is.EqualTo(_heads.Safe.Hash));
        }
    }

    [Test]
    public async Task Advance_DeadlineAfterSomeBlocks_RetainsValidatedProgress()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 3, extraData: 0xb6));
        IUnsafeBlockPreparation preparation = Substitute.For<IUnsafeBlockPreparation>();
        int calls = 0;
        preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 3) throw new OperationCanceledException();
            return new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.Ready);
        });
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance, preparation);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _follower.Advance(_heads, CancellationToken.None));
        Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[1].Hash));

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[2].Hash));
    }

    [Test]
    public async Task Advance_MissingSourceHash_IsRetryableWithoutEngineCalls([Values] bool numbered)
    {
        EezL1Block latest = new() { Number = 100, Hash = numbered ? Keccak.Compute("source") : null! };
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(latest);
        if (numbered)
            _sequencer.GetBlockByNumber(_heads.Head.Number + UnsafeHeadFollower.MaxBlocksPerAdvance, Arg.Any<CancellationToken>())
                .Returns(new EezL1Block { Number = _heads.Head.Number + UnsafeHeadFollower.MaxBlocksPerAdvance, Hash = null! });

        await Assert.ThrowsAsync<L1SourceIncompleteException>(() => _follower.Advance(_heads, CancellationToken.None));

        Assert.That(_engine.Inserted, Is.Empty);
    }

    [Test]
    public async Task Advance_KnownHeadLosesL1History_ReturnsToSafeWithoutCachedAcceptance([Values] bool sourceRetreats)
    {
        Block[] ahead = Serve(Branch(_heads.Safe, sourceRetreats ? 2 : 1, extraData: 0xb7));
        IUnsafeBlockPreparation preparation = Substitute.For<IUnsafeBlockPreparation>();
        preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.Ready));
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance, preparation);
        await _follower.Advance(_heads, CancellationToken.None);
        Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[^1].Hash));
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = ahead[0].Number, Hash = ahead[0].Hash! });
        _engine.Inserted.Clear();
        preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.SourceNotCanonical));

        await _follower.Advance(_heads, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_heads.Head.Hash, Is.EqualTo(_heads.Safe.Hash));
            Assert.That(_engine.Inserted, Is.Empty);
        }
    }

    [Test]
    public async Task Advance_DeadlineBeforeFirstInsertion_ReducesBatchUntilTheNextPollCanAdvance([Values] bool duringPreparation)
    {
        Block[] ahead = Serve(Branch(_heads.Safe, UnsafeHeadFollower.MaxBlocksPerAdvance, extraData: 0xb8));
        IUnsafeBlockPreparation preparation = Substitute.For<IUnsafeBlockPreparation>();
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance, preparation);
        Dictionary<Hash256, byte[]> encoded = ahead.ToDictionary(static block => block.Hash!, static block => Rlp.Encode(block).Bytes);
        Hash256 safe = _heads.Safe.Hash!, finalized = _heads.Finalized.Hash!;
        for (int poll = 0; poll < 3; poll++)
        {
            int reads = 0;
            using CancellationTokenSource deadline = new();
            _sequencer.GetRawBlock(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                if (++reads > UnsafeHeadFollower.MaxBlocksPerAdvance / 4 && !duringPreparation)
                {
                    deadline.Cancel();
                    return Task.FromCanceled<byte[]?>(deadline.Token);
                }
                return Task.FromResult<byte[]?>(encoded[call.ArgAt<Hash256>(0)]);
            });
            preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                if (duringPreparation && reads > UnsafeHeadFollower.MaxBlocksPerAdvance / 4)
                {
                    deadline.Cancel();
                    return ValueTask.FromCanceled<UnsafeBlockPreparationResult>(deadline.Token);
                }
                return new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.Ready);
            });
            if (poll < 2)
            {
                await Assert.CatchAsync<OperationCanceledException>(() => _follower.Advance(_heads, deadline.Token));
                Assert.That(_engine.Inserted, Is.Empty, "the first attempt did not complete an insertion");
            }
            else
                await _follower.Advance(_heads, deadline.Token);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[UnsafeHeadFollower.MaxBlocksPerAdvance / 4 - 1].Hash));
            Assert.That(_heads.Safe.Hash, Is.EqualTo(safe));
            Assert.That(_heads.Finalized.Hash, Is.EqualTo(finalized));
        }
    }

    [Test]
    public async Task Advance_RestartBelowStoredCanonicalBlocks_RevalidatesThemBeforeMovingTheHead()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 2, extraData: 0xb9));
        await _follower.Advance(_heads, CancellationToken.None);
        _engine.Inserted.Clear();
        _sequencer.ClearReceivedCalls();
        FollowerHeads restarted = new(_engine, _chain, _heads.Safe);
        IUnsafeBlockPreparation preparation = Substitute.For<IUnsafeBlockPreparation>();
        preparation.Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnsafeBlockPreparationResult>(UnsafeBlockPreparationResult.Ready));
        UnsafeHeadFollower follower = new(_sequencer, _chain, _engine,
            new Rollup0HeaderRules(DerivedChain.SpecProvider, DerivedChain.Context), LimboLogs.Instance, preparation);

        await follower.Advance(restarted, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_engine.Inserted.Select(static block => block.Hash), Is.EqualTo(ahead.Select(static block => block.Hash)));
            Assert.That(restarted.Head.Hash, Is.EqualTo(ahead[^1].Hash));
            Assert.That(restarted.Safe.Hash, Is.EqualTo(_heads.Safe.Hash));
            Assert.That(RawBlockReads(), Is.Zero, "stored bodies are reused, but their canonical evidence and Engine validation are not skipped");
        }
        await preparation.Received(2).Prepare(Arg.Any<BlockHeader>(), Arg.Any<Block>(), Arg.Any<CancellationToken>());
    }

    private int RawBlockReads() => _sequencer.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IEezSequencerApi.GetRawBlock));

    private Block[] Serve(Block[] blocks)
    {
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Hash = blocks[^1].Hash!, Number = blocks[^1].Number });
        foreach (Block block in blocks)
        {
            _sequencer.GetRawBlock(block.Hash!, Arg.Any<CancellationToken>()).Returns(Rlp.Encode(block).Bytes);
            _sequencer.GetBlockByNumber(block.Number, Arg.Any<CancellationToken>()).Returns(new EezL1Block { Number = block.Number, Hash = block.Hash! });
        }

        return blocks;
    }

    private static Block[] Branch(BlockHeader parent, int count, byte extraData)
    {
        List<Block> blocks = [];
        for (int i = 0; i < count; i++)
        {
            Block block = DerivedChain.Child(parent, TestItem.AddressA, [extraData, (byte)i]);
            blocks.Add(block);
            parent = block.Header;
        }

        return [.. blocks];
    }
}
