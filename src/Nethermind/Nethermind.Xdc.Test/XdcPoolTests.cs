// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Types;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class XdcPoolTests
{
    private static BlockRoundInfo MakeBlockInfo(ulong round = 1) =>
        new(Hash256.Zero, round, (long)round);

    private static Vote BuildVote(BlockRoundInfo info, PrivateKey key, ulong gap = 0)
        => XdcTestHelper.BuildSignedVote(info, gap, key);

    [Test]
    public void Add_NullSigner_Throws()
    {
        XdcPool<Vote> pool = new();
        Vote vote = new(MakeBlockInfo(), 0); // no Signer set

        pool.Invoking(p => p.Add(vote)).Should().Throw<ArgumentException>();
    }

    [Test]
    public void Add_SameVoteTwice_CountIsOne()
    {
        XdcPool<Vote> pool = new();
        Vote vote = BuildVote(MakeBlockInfo(), TestItem.PrivateKeyA);

        pool.Add(vote);
        pool.Add(vote);

        pool.GetCount(vote).Should().Be(1);
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
        vote1.PoolKey().Should().Be(vote2.PoolKey());
        vote1.Should().NotBe(vote2);

        pool.Add(vote1);
        pool.Add(vote2);

        // only one — signer dedup prevents the second vote from being added
        pool.GetCount(vote1).Should().Be(1);
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

        pool.GetCount(vote1).Should().Be(2);
    }

    [Test]
    public void EndRound_RemovesItemsUpToRound()
    {
        XdcPool<Vote> pool = new();
        Vote voteRound1 = BuildVote(MakeBlockInfo(round: 1), TestItem.PrivateKeyA);
        Vote voteRound2 = BuildVote(MakeBlockInfo(round: 2), TestItem.PrivateKeyA);

        pool.Add(voteRound1);
        pool.Add(voteRound2);
        pool.EndRound(1);

        pool.GetCount(voteRound1).Should().Be(0);
        pool.GetCount(voteRound2).Should().Be(1);
    }
}
