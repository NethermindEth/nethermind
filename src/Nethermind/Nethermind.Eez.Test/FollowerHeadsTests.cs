// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class FollowerHeadsTests
{
    private IBlockTree _chain = null!;
    private ChainEngine _engine = null!;
    private FollowerHeads _heads = null!;

    [SetUp]
    public void SetUp()
    {
        _chain = Build.A.BlockTree().OfChainLength(6).TestObject;
        _engine = new ChainEngine(_chain);
        _heads = new FollowerHeads(_engine, _chain, _chain.Genesis!);
    }

    [Test]
    public async Task AdvanceSafe_HeadBuildsOnIt_KeepsTheHead()
    {
        await _heads.SetHead(Block(5));

        await _heads.AdvanceSafe(Block(3));

        Assert.That((_heads.Head.Number, _heads.Safe.Number), Is.EqualTo((5UL, 3UL)), "blocks derived above the settled one stay the head");
    }

    [Test]
    public async Task AdvanceSafe_AboveTheHead_MovesTheHeadWithIt()
    {
        await _heads.SetHead(Block(2));

        await _heads.AdvanceSafe(Block(4));

        Assert.That(_heads.Head.Number, Is.EqualTo(4), "the head is never below safe");
    }

    [Test]
    public async Task AdvanceSafe_OffTheHeadsChain_MovesTheHeadToIt()
    {
        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.IsMainChain(Arg.Any<BlockHeader>()).Returns(false);
        FollowerHeads heads = new(Substitute.For<IEezL2Engine>(), tree, _chain.Genesis!);
        await heads.SetHead(Block(5));

        await heads.AdvanceSafe(Block(3));

        Assert.That(heads.Head.Number, Is.EqualTo(3), "a head on another branch does not build on the new safe");
    }

    [Test]
    public async Task SetHead_BelowSafe_Throws()
    {
        await _heads.AdvanceSafe(Block(3));

        Assert.ThrowsAsync<EezFollowerException>(() => _heads.SetHead(Block(2)), "the head cannot drop below what L1 settled");
    }

    [Test]
    public async Task Reset_BelowFinalized_Throws()
    {
        await _heads.AdvanceSafe(Block(3));
        await _heads.AdvanceFinalized(Block(3));

        Assert.ThrowsAsync<EezFollowerException>(() => _heads.Reset(Block(2)), "finalized never moves back");
    }

    [Test]
    public async Task AdvanceFinalized_AboveSafe_Throws()
    {
        await _heads.AdvanceSafe(Block(2));

        Assert.ThrowsAsync<EezFollowerException>(() => _heads.AdvanceFinalized(Block(3)), "finalized cannot pass safe");
    }

    [Test]
    public void AdvanceSafe_EngineDoesNotApplyIt_KeepsTheHeads()
    {
        IEezL2Engine busy = Substitute.For<IEezL2Engine>();
        busy.UpdateForkchoice(Arg.Any<Hash256>(), Arg.Any<Hash256>(), Arg.Any<Hash256>()).Returns(Task.FromException(new EezEngineUnavailableException("busy")));
        FollowerHeads heads = new(busy, _chain, _chain.Genesis!);

        Assert.ThrowsAsync<EezEngineUnavailableException>(() => heads.AdvanceSafe(Block(3)), "precondition: the engine refuses the move");
        Assert.That(heads.Safe.Number, Is.Zero, "a move the node did not take is not recorded, so the retry makes it again");
    }

    private BlockHeader Block(ulong number) => _chain.FindHeader(number, BlockTreeLookupOptions.None)!;
}
