// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Validators;

/// <summary>Port of Besu's <c>VoteTallyCacheTest</c>, <c>ForkingVoteTallyCacheTest</c> and <c>EpochManagerTest</c>.</summary>
[Parallelizable(ParallelScope.All)]
public class VoteTallyCacheTests
{
    private static readonly QbftBlockInterface BlockInterface = new(QbftOnlyCodecSelector.Instance);
    private static readonly Address[] Validators = [QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3)];

    private sealed class Chain
    {
        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public List<BlockHeader> Headers { get; } = [];

        public BlockHeader Add(Address proposer, Vote? vote, IReadOnlyList<Address>? validators = null)
        {
            ulong number = (ulong)Headers.Count;
            byte[] extraData = QbftExtraDataCodec.Instance.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], vote, 0, validators ?? []));
            BlockHeader header = QbftTestData.BftHeader(number, proposer, extraData)
                .WithParentHash(Headers.Count == 0 ? Keccak.Zero : Headers[^1].Hash!)
                .TestObject;
            QbftBlockHeader qbft = QbftTestData.ToQbftHeader(header);
            BlockTree.FindHeader(qbft.Hash!, Arg.Any<BlockTreeLookupOptions>()).Returns(qbft);
            Headers.Add(qbft);
            return qbft;
        }
    }

    private static VoteTallyCache Cache(Chain chain, long epochLength = 30_000) =>
        new(chain.BlockTree, new VoteTallyUpdater(new EpochManager(epochLength), BlockInterface), new EpochManager(epochLength), BlockInterface);

    [Test]
    public void GenesisValidatorsComeFromTheEpochBlockExtraData()
    {
        Chain chain = new();
        BlockHeader genesis = chain.Add(Address.Zero, null, Validators);
        Assert.That(Cache(chain).GetVoteTallyAfterBlock(genesis).Validators, Is.EqualTo(Validators));
    }

    [Test]
    public void VotesAccumulateAlongTheChainAndChangeTheValidatorSet()
    {
        Chain chain = new();
        chain.Add(Address.Zero, null, Validators);
        Address newcomer = QbftTestData.Addr(4);
        BlockHeader afterOneVote = chain.Add(Validators[0], Vote.AuthVote(newcomer), Validators);
        BlockHeader afterTwoVotes = chain.Add(Validators[1], Vote.AuthVote(newcomer), Validators);

        VoteTallyCache cache = Cache(chain);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetVoteTallyAfterBlock(afterOneVote).Validators, Is.EqualTo(Validators));
            Assert.That(cache.GetVoteTallyAfterBlock(afterTwoVotes).Validators, Is.EqualTo(new[] { Validators[0], Validators[1], Validators[2], newcomer }));
        }
    }

    [Test]
    public void EpochBlockResetsOutstandingVotesAndRestartsFromItsValidatorList()
    {
        Chain chain = new();
        chain.Add(Address.Zero, null, Validators);
        chain.Add(Validators[0], Vote.AuthVote(QbftTestData.Addr(4)), Validators);
        Address[] epochValidators = [Validators[0], Validators[1]];
        BlockHeader epoch = chain.Add(Validators[1], null, epochValidators); // block 2 = epoch block with epoch length 2
        BlockHeader afterEpoch = chain.Add(Validators[0], Vote.AuthVote(QbftTestData.Addr(4)), epochValidators);

        VoteTallyCache cache = Cache(chain, epochLength: 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetVoteTallyAfterBlock(epoch).Validators, Is.EqualTo(epochValidators));
            Assert.That(cache.GetVoteTallyAfterBlock(afterEpoch).Validators, Is.EqualTo(epochValidators), "the pre-epoch vote was discarded, a single new vote is not a majority of two");
        }
    }

    [Test]
    public void OrphanedChainThrows()
    {
        Chain chain = new();
        BlockHeader header = QbftTestData.ToQbftHeader(QbftTestData.BftHeader(7, Address.Zero, QbftExtraDataCodec.Instance.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, 0, Validators))).WithParentHash(Keccak.Compute("missing")).TestObject);
        Assert.That(() => Cache(chain).GetVoteTallyAfterBlock(header), Throws.InvalidOperationException);
    }

    [Test]
    public void ForkingCacheInstallsTheTransitionValidatorsAtTheBlockBeforeTheFork()
    {
        Chain chain = new();
        chain.Add(Address.Zero, null, Validators);
        BlockHeader block1 = chain.Add(Validators[0], null, Validators);
        Address[] forkValidators = [QbftTestData.Addr(7), QbftTestData.Addr(8)];
        QbftForksSchedule schedule = QbftForksSchedule.Create(new QbftChainSpecEngineParameters { Transitions = [new QbftTransition { Block = 2, Validators = forkValidators }] }, ulong.MaxValue);
        ForkingVoteTallyCache cache = new(chain.BlockTree, new VoteTallyUpdater(new EpochManager(30_000), BlockInterface), new EpochManager(30_000), BlockInterface, schedule);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetVoteTallyAfterBlock(chain.Headers[0]).Validators, Is.EqualTo(Validators));
            Assert.That(cache.GetVoteTallyAfterBlock(block1).Validators, Is.EqualTo(forkValidators), "validators after block 1 are the ones that must sign block 2");
        }
    }

    [TestCase(30_000, 0, true)]
    [TestCase(30_000, 1, false)]
    [TestCase(30_000, 30_000, true)]
    [TestCase(30_000, 60_000, true)]
    [TestCase(30_000, 60_001, false)]
    public void EpochManagerIdentifiesEpochBlocks(long epochLength, long block, bool isEpoch)
    {
        EpochManager manager = new(epochLength);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.IsEpochBlock(block), Is.EqualTo(isEpoch));
            Assert.That(manager.GetLastEpochBlock(block), Is.EqualTo(block / epochLength * epochLength));
        }
    }

    [Test]
    public void EpochManagerWithStartBlockCountsFromTheMigrationBlock()
    {
        // Besu semantics: the last IBFT 2.0 block (startBlock - 1) is the epoch block that seeds the first QBFT validator set.
        EpochManager manager = new(100, startBlock: 250);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.IsEpochBlock(100), Is.False);
            Assert.That(manager.IsEpochBlock(249), Is.True);
            Assert.That(manager.IsEpochBlock(250), Is.False);
            Assert.That(manager.IsEpochBlock(349), Is.True);
            Assert.That(manager.GetLastEpochBlock(360), Is.EqualTo(350));
            Assert.That(() => manager.GetLastEpochBlock(10), Throws.ArgumentException);
        }
    }

    [Test]
    public void ZeroEpochLengthIsRejected() =>
        Assert.That(() => new EpochManager(0), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
}
