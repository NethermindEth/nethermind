// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
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
        _heads = new FollowerHeads(_engine, _chain, _chain.Genesis!);
        await _heads.AdvanceSafe(_chain.FindHeader(2UL)!);
        _engine.Forkchoices.Clear();
        _follower = new UnsafeHeadFollower(_sequencer, _chain, _engine, DerivedChain.SpecProvider, DerivedChain.Context, LimboLogs.Instance);
    }

    [Test]
    public async Task Advance_SequencerAheadOfSafe_InsertsItsBlocksInOrder()
    {
        Block[] ahead = Serve(Branch(_heads.Safe, 2, extraData: 0xaa));

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(_engine.Inserted.Select(static b => b.Number), Is.EqualTo(new ulong[] { 3, 4 }), "the missing blocks enter the chain oldest first");
        Assert.That(_chain.Head!.Hash, Is.EqualTo(ahead[^1].Hash), "the head is the sequencer's");
        Assert.That(_heads.Head.Hash, Is.EqualTo(ahead[^1].Hash), "the follower's heads record the new head");
        Assert.That(_engine.Forkchoices[^1].Safe, Is.EqualTo(_heads.Safe.Hash), "the sequencer never moves safe");
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

        Assert.That(_chain.Head!.Hash, Is.EqualTo(ahead[^1].Hash), "the sequencer's head is the head again");
        Assert.That(RawBlockReads(), Is.Zero, "blocks the node already holds are not fetched again");
    }

    [Test]
    public async Task Advance_SequencerTooFarAhead_WaitsForL1()
    {
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>())
            .Returns(new EezL1Block { Hash = Keccak.Compute("far ahead"), Number = _heads.Head.Number + UnsafeHeadFollower.MaxDepth + 1 });

        await _follower.Advance(_heads, CancellationToken.None);

        Assert.That(RawBlockReads(), Is.Zero, "a sequencer far ahead of the local head is not walked back block by block");
    }

    private int RawBlockReads() => _sequencer.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IEezSequencerApi.GetRawBlock));

    private Block[] Serve(Block[] blocks)
    {
        _sequencer.GetLatestBlock(Arg.Any<CancellationToken>()).Returns(new EezL1Block { Hash = blocks[^1].Hash!, Number = blocks[^1].Number });
        foreach (Block block in blocks)
        {
            _sequencer.GetRawBlock(block.Hash!, Arg.Any<CancellationToken>()).Returns(Rlp.Encode(block).Bytes);
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
