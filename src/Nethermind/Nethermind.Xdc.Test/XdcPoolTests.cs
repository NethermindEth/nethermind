// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using Nethermind.Xdc.Test.Helpers;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcPoolTests
{
    private static BlockRoundInfo MakeBlockInfo(ulong round = 1) =>
        new(Hash256.Zero, round, round);

    private static Vote BuildVote(BlockRoundInfo info, PrivateKey key, ulong gap = 0)
        => XdcTestHelper.BuildSignedVote(info, gap, key);

    [Test]
    public void Add_NullSigner_Throws()
    {
        XdcPool<Vote> pool = new();
        Vote vote = new(MakeBlockInfo(), 0); // no Signer set

        Assert.That(() => pool.Add(vote), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Add_SameVoteTwice_CountIsOne()
    {
        XdcPool<Vote> pool = new();
        Vote vote = BuildVote(MakeBlockInfo(), TestItem.PrivateKeyA);

        pool.Add(vote);
        pool.Add(vote);

        Assert.That(pool.GetCount(vote), Is.EqualTo(1));
    }

    [Test]
    public void Add_SameSignerDifferentSignature_CountIsOne()
    {
        // A byzantine node could use a non-deterministic k to produce two valid signatures
        // for the same vote content. The pool must treat both as one vote from that signer.
        XdcPool<Vote> pool = new();
        BlockRoundInfo info = MakeBlockInfo();
        Vote vote1 = BuildVote(info, TestItem.PrivateKeyA);
        Vote vote2 = new(info, 0)
        {
            Signature = new Signature(new byte[65]),
            Signer = TestItem.PrivateKeyA.Address
        };

        // Same content and same signer, but not equal since signature is different
        Assert.That(vote1.PoolKey(), Is.EqualTo(vote2.PoolKey()));
        Assert.That(vote1, Is.Not.EqualTo(vote2));

        pool.Add(vote1);
        pool.Add(vote2);

        // only one — signer dedup prevents the second vote from being added
        Assert.That(pool.GetCount(vote1), Is.EqualTo(1));
    }

    [Test]
    public void Add_DifferentSigners_CountIsTwo()
    {
        XdcPool<Vote> pool = new();
        BlockRoundInfo info = MakeBlockInfo();
        Vote vote1 = BuildVote(info, TestItem.PrivateKeyA);
        Vote vote2 = BuildVote(info, TestItem.PrivateKeyB);

        pool.Add(vote1);
        pool.Add(vote2);

        Assert.That(pool.GetCount(vote1), Is.EqualTo(2));
    }

    [Test]
    public void EndRound_RemovesItemsUpToRound()
    {
        XdcPool<Vote> pool = new();
        Vote voteRound1 = BuildVote(MakeBlockInfo(round: 1), TestItem.PrivateKeyA);
        Vote voteRound2 = BuildVote(MakeBlockInfo(round: 2), TestItem.PrivateKeyA);

        pool.Add(voteRound1);
        pool.Add(voteRound2);
        pool.EndRound(1UL);

        Assert.That(pool.GetCount(voteRound1), Is.EqualTo(0));
        Assert.That(pool.GetCount(voteRound2), Is.EqualTo(1));
    }

    [Test]
    public void RemoveRoundsOutsideRetention_ExpiredRounds_RemovesOnlyExpiredRounds()
    {
        const ulong latestRound = 12;
        const ulong retainedRoundCount = XdcConstants.PoolHygieneRound;
        const ulong expiredRound = latestRound - retainedRoundCount;
        const ulong oldestRetainedRound = expiredRound + 1;

        XdcPool<Vote> pool = new();
        Vote expiredVote = BuildVote(MakeBlockInfo(expiredRound), TestItem.PrivateKeyA);
        Vote oldestRetainedVote = BuildVote(MakeBlockInfo(oldestRetainedRound), TestItem.PrivateKeyA);
        Vote latestVote = BuildVote(MakeBlockInfo(latestRound), TestItem.PrivateKeyA);

        pool.Add(expiredVote);
        pool.Add(oldestRetainedVote);
        pool.Add(latestVote);

        pool.RemoveRoundsOutsideRetention(latestRound, retainedRoundCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.GetCount(expiredVote), Is.Zero);
            Assert.That(pool.GetCount(oldestRetainedVote), Is.EqualTo(1));
            Assert.That(pool.GetCount(latestVote), Is.EqualTo(1));
        }
    }
}
