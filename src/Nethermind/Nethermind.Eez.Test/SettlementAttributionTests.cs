// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SettlementAttributionTests
{
    private const ulong Block = 76;
    private static readonly Hash256 BlockHash = Keccak.Compute("block");
    private static readonly Hash256 OtherFork = Keccak.Compute("other fork");
    private static readonly ValueHash256 A = Root(0x0a);
    private static readonly ValueHash256 B = Root(0x0b);
    private static readonly ValueHash256 C = Root(0x0c);
    private static readonly ValueHash256 D = Root(0x0d);

    [Test]
    public void Attribute_WholeChainRan_EndsAtTheClaimedEnd() =>
        Assert.That(Settle([B, C], B, C), Is.EqualTo(new L1Settlement(0, 2, C, A)), "every claimed step ran");

    [Test]
    public void Attribute_PrefixRan_EndsAtTheLastStepThatRan() =>
        Assert.That(Settle([B, C, D], B, C), Is.EqualTo(new L1Settlement(0, 2, C, A)), "the endpoint is the last root that ran");

    [Test]
    public void Attribute_NothingRan_IsEmpty() =>
        Assert.That(Settle([B]).IsEmpty, Is.True, "an empty window settles nothing");

    /// <summary>
    /// A competing batch in the same block already made the first hop:
    /// 1. ours claims A to B to C, but only B to C runs;
    /// 2. the run starts at B, so the effect slice skips nothing and takes the one effect.
    /// </summary>
    [Test]
    public void Attribute_FirstHopAlreadyMade_ResumesAfterIt()
    {
        L1Settlement settlement = Settle([B, C], C);

        Assert.That(settlement, Is.EqualTo(new L1Settlement(1, 1, C, B)), "the run starts at the hop the competitor made, not at the claimed current state");
        Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(0, 1)), "the anchor was skipped, the effect ran");
    }

    [Test]
    public void Attribute_RunResumesMidChain_SkipsTheEffectsBeforeIt()
    {
        L1Settlement settlement = Settle([B, C, D], D);

        Assert.That(settlement, Is.EqualTo(new L1Settlement(2, 1, D, C)), "only the last hop ran, from the root the competitor left");
        Assert.That(settlement.Effects, Is.EqualTo(new ProducingSlice(1, 1)), "the effect the competitor settled is skipped");
    }

    [Test]
    public void Attribute_RepeatedRoot_MatchesByPosition() =>
        Assert.That(Settle([B, B, C], B, B), Is.EqualTo(new L1Settlement(0, 2, B, A)), "a repeated root is located, not counted twice");

    [Test]
    public void Attribute_RootsOutOfOrder_IsEmpty() =>
        Assert.That(Settle([B, C], C, B).IsEmpty, Is.True, "no consecutive run of the claimed chain matches");

    [Test]
    public void Attribute_RootTheBatchNeverClaimed_IsEmpty() =>
        Assert.That(Settle([B], D).IsEmpty, Is.True, "roots from outside the batch's claims are not credited to it");

    /// <summary>
    /// Two composers post for our rollup in one L1 block:
    /// 1. the first, at transaction 1, claims A to B and runs;
    /// 2. the second, at transaction 3, claims B to C and runs.
    /// Each owns only the roots between its transaction and the next one that lists our rollup.
    /// </summary>
    [Test]
    public void Attribute_TwoBatchesInOneBlock_EachOwnsItsWindow()
    {
        L1Batch first = Batch(1, A, [B]);
        L1Batch second = Batch(3, B, [C]);
        SettledRoot[] roots = [Settled(1, 0, B), Settled(3, 0, C)];

        L1Settlement[] settlements = SettlementAttribution.Attribute([first, second], roots);

        Assert.That(settlements, Is.EqualTo(new[] { new L1Settlement(0, 1, B, A), new L1Settlement(0, 1, C, B) }),
            "the first batch is not credited with the second one's hop");
    }

    [Test]
    public void Attribute_LoserInTheSameBlock_IsEmpty()
    {
        L1Batch winner = Batch(1, A, [B]);
        L1Batch loser = Batch(2, A, [D]);

        L1Settlement[] settlements = SettlementAttribution.Attribute([winner, loser], [Settled(1, 0, B)]);

        Assert.That(settlements[1].IsEmpty, Is.True, "the loser's window holds none of the winner's roots");
    }

    [Test]
    public void Attribute_RootOnAnotherForkOfTheBlock_Throws() =>
        Assert.Throws<L1SourceIncompleteException>(() => SettlementAttribution.Attribute([Batch(1, A, [B])], [Settled(1, 0, B, OtherFork)]),
            "the two log reads straddled a reorg and must be retried");

    [Test]
    public void Of_PostBatch_ClaimsOurRollupsChain()
    {
        const ulong rollup = 1;
        ExecutionEntry[] entries =
        [
            Entry(new StateUpdate(rollup, A, B, default), new StateUpdate(2, D, D, default)),
            Entry(new StateUpdate(rollup, B, C, default)),
        ];
        PostBatch batch = new([], entries, [], default, default, [], [new RollupProofSystems(rollup, [0])], [], [], [], 0, false);

        L1Batch l1 = L1Batch.Of(batch, rollup, Block, BlockHash, Keccak.Compute("tx"), 4);

        Assert.That(l1.ClaimedCurrentState, Is.EqualTo(A), "the first update's current state");
        Assert.That(l1.ClaimedChain, Is.EqualTo(new[] { B, C }), "our rollup's new states only, in entry order");
        Assert.That(l1.VerifiesOurRollup, Is.True, "the batch lists our rollup");
    }

    private static L1Settlement Settle(ValueHash256[] claimed, params ValueHash256[] settled) =>
        SettlementAttribution.Attribute([Batch(1, A, claimed)], settled.Select(static (root, i) => Settled(1, (ulong)i, root)).ToArray())[0];

    private static L1Batch Batch(ulong transactionIndex, ValueHash256 current, ValueHash256[] chain) =>
        new(Block, BlockHash, Keccak.Compute($"tx {transactionIndex}"), transactionIndex, true, current, chain, default);

    private static SettledRoot Settled(ulong transactionIndex, ulong logIndex, ValueHash256 root, Hash256? blockHash = null) =>
        new(Block, blockHash ?? BlockHash, transactionIndex, logIndex, root);

    private static ExecutionEntry Entry(params StateUpdate[] updates) => new(updates, default, [], [], default, 0, true, []);

    private static ValueHash256 Root(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());
}
