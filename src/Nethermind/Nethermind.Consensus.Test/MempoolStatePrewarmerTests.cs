// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class MempoolStatePrewarmerTests
{
    // Slot 12, so the arrival grace is 4s: mid-slot, on the boundary and within the grace after it all stay on that
    // boundary - the block for it is still in flight; only past the grace is a missed slot the better bet. A clock
    // behind the parent still moves forward, and a head that arrived a slot late keeps the boundary it can still get.
    [TestCase(100UL, 105UL, 112UL)]
    [TestCase(100UL, 112UL, 112UL)]
    [TestCase(100UL, 113UL, 112UL)]
    [TestCase(100UL, 116UL, 112UL)]
    [TestCase(100UL, 117UL, 124UL)]
    [TestCase(100UL, 128UL, 124UL)]
    [TestCase(100UL, 129UL, 136UL)]
    [TestCase(100UL, 90UL, 112UL)]
    public void PredictNextTimestamp_ReturnsTheFirstSlotBoundaryThatCanStillArrive(ulong parent, ulong now, ulong expected) =>
        Assert.That(MempoolStatePrewarmer.PredictNextTimestamp(parent, now, secondsPerSlot: 12), Is.EqualTo(expected));

    [Test]
    public void SelectDelta_WhenEmpty_ReturnsEmpty([Values] bool missingSender)
    {
        Transaction[] delta = MempoolStatePrewarmer.SelectDelta(missingSender ? [new Transaction()] : [], []);

        Assert.That(delta, Is.Empty, "an empty selection yields no transactions to warm");
    }

    [Test]
    public void SelectDelta_FirstPass_SelectsEverySender([Values] bool reused)
    {
        Transaction[] ordered = [.. BuildSenderTxs(TestItem.PrivateKeyA, 3), .. BuildSenderTxs(TestItem.PrivateKeyB, 2)];
        Dictionary<AddressAsKey, int> warmedPerSender = [];
        if (reused)
        {
            MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);
            warmedPerSender.Clear();
        }

        Transaction[] delta = MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);

        Assert.That(delta.Length, Is.EqualTo(5), "the first pass warms every selected transaction");
        Assert.That(warmedPerSender[TestItem.AddressA], Is.EqualTo(3), "sender A's warmed count is recorded");
        Assert.That(warmedPerSender[TestItem.AddressB], Is.EqualTo(2), "sender B's warmed count is recorded");
    }

    [Test]
    public void SelectDelta_SecondPass_SkipsAlreadyWarmedSenders()
    {
        Transaction[] ordered = [.. BuildSenderTxs(TestItem.PrivateKeyA, 3)];
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDelta(ordered, warmedPerSender);

        Assert.That(secondPass, Is.Empty, "a sender whose whole selected set is already warmed is skipped on the next pass");
    }

    [Test]
    public void SelectDelta_SecondPass_ReplaysFullGroupWhenSenderGrows()
    {
        Dictionary<AddressAsKey, int> warmedPerSender = [];

        MempoolStatePrewarmer.SelectDelta(BuildSenderTxs(TestItem.PrivateKeyA, 2), warmedPerSender);
        // A later-nonce transaction arrives for the same sender.
        Transaction[] secondPass = MempoolStatePrewarmer.SelectDelta(BuildSenderTxs(TestItem.PrivateKeyA, 4), warmedPerSender);

        Assert.That(secondPass.Length, Is.EqualTo(4), "when new transactions arrive the sender's full group is replayed so predecessors are present");
    }

    [Test]
    public void SelectDelta_ReusedScratchPreservesInterleavedSenderOrder([Values(2, 257)] int count)
    {
        Transaction[] a = BuildSenderTxs(TestItem.PrivateKeyA, count);
        Transaction[] b = BuildSenderTxs(TestItem.PrivateKeyB, count);
        Transaction[] interleaved = a.Zip(b).SelectMany(pair => new[] { pair.First, pair.Second }).ToArray();
        Dictionary<AddressAsKey, int> warmed = [];
        Dictionary<AddressAsKey, MempoolStatePrewarmer.SenderSelection> scratch = [];

        Transaction[] first = MempoolStatePrewarmer.SelectDelta(interleaved, warmed, scratch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(a.Concat(b)));
            Assert.That(scratch, Is.Empty);
        }
        Assert.That(MempoolStatePrewarmer.SelectDelta(interleaved, warmed, scratch), Is.Empty);

        Transaction[] extended = BuildSenderTxs(TestItem.PrivateKeyB, count + 1);
        Assert.That(MempoolStatePrewarmer.SelectDelta(extended.Concat(a), warmed, scratch), Is.EqualTo(extended));
        Assert.That(scratch, Is.Empty);
    }

    [Test]
    public void SelectDelta_ReusedScratchRecoversAfterSourceThrows()
    {
        Transaction[] transactions = BuildSenderTxs(TestItem.PrivateKeyA, 2);
        Dictionary<AddressAsKey, int> warmed = [];
        Dictionary<AddressAsKey, MempoolStatePrewarmer.SenderSelection> scratch = [];
        Assert.Throws<InvalidOperationException>(() => MempoolStatePrewarmer.SelectDelta(FailingSource(), warmed, scratch));
        Assert.That(MempoolStatePrewarmer.SelectDelta(transactions, warmed, scratch), Is.EqualTo(transactions));

        IEnumerable<Transaction> FailingSource()
        {
            yield return transactions[0];
            throw new InvalidOperationException();
        }
    }

    [Test]
    public async Task PreWarmFromMempool_PassesHeaderPreservingChainSpecificSubtypeToPreWarmer()
    {
        ChainSpecificHeader parentHeader = new(
            TestItem.KeccakA, Keccak.OfAnEmptySequenceRlp, TestItem.AddressA, UInt256.One, 10, 30_000_000, 100, [])
        {
            Hash = TestItem.KeccakB,
            MixHash = TestItem.KeccakC,
            ParentBeaconBlockRoot = TestItem.KeccakD,
            BaseFeePerGas = 7,
            Author = TestItem.AddressD,
        };
        Block head = new(parentHeader);

        ITxSource txSource = Substitute.For<ITxSource>();
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns(BuildSenderTxs(TestItem.PrivateKeyA, 1));
        IBlockProducerTxSourceFactory txSourceFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        txSourceFactory.Create().Returns(txSource);

        IBlockTree blockTree = Substitute.For<IBlockTree>();

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(London.Instance);

        DateTime headTime = DateTimeOffset.FromUnixTimeSeconds((long)parentHeader.Timestamp).UtcDateTime;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNow.Returns(headTime);
        timestamper.UnixTime.Returns(new UnixTime(headTime));

        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.PreWarming.Returns(PreWarmMode.BlockAndMempool);
        blocksConfig.SecondsPerSlot.Returns(12ul);

        DeltaCapturingPreWarmer preWarmer = new();

        using MempoolStatePrewarmer _ = new(
            preWarmer, txSourceFactory, blockTree, specProvider, timestamper, blocksConfig, LimboLogs.Instance);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(head));

        BlockHeader deltaHeader = await preWarmer.CapturedHeader.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaHeader, Is.InstanceOf<ChainSpecificHeader>(), "chain-specific header subtypes must survive so chain-specific processors don't hit an InvalidCastException");
            Assert.That(deltaHeader.Number, Is.EqualTo(parentHeader.Number + 1), "the child is the parent's successor");
            Assert.That(deltaHeader.Timestamp, Is.EqualTo(parentHeader.Timestamp + blocksConfig.SecondsPerSlot),
                "the predicted header must sit on the next slot boundary: the EIP-4788 cells warmed for it are indexed by its timestamp");
            Assert.That(deltaHeader.MixHash, Is.EqualTo(parentHeader.MixHash), "MixHash is propagated from the parent");
            Assert.That(deltaHeader.ParentBeaconBlockRoot, Is.EqualTo(parentHeader.ParentBeaconBlockRoot), "ParentBeaconBlockRoot is propagated from the parent");
            Assert.That(deltaHeader.BaseFeePerGas, Is.EqualTo(BaseFeeCalculator.Calculate(parentHeader, London.Instance)), "BaseFeePerGas is recalculated for the child");
            Assert.That(deltaHeader.GasBeneficiary, Is.EqualTo(parentHeader.GasBeneficiary), "Beneficiary resolves to the parent's actual coinbase (Author), not a diverging governance vote target");
            txSource.Received(1).GetTransactions(parentHeader, deltaHeader, deltaHeader.GasLimit);
        }
    }

    // Stands in for a chain-specific header subtype (e.g. XdcBlockHeader) whose CreateSimulatedChild returns its own type.
    private sealed class ChainSpecificHeader(
        Hash256 parentHash, Hash256 unclesHash, Address beneficiary, in UInt256 difficulty,
        ulong number, ulong gasLimit, ulong timestamp, byte[] extraData)
        : BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
    {
        public override BlockHeader CreateSimulatedChild(ulong timestamp) =>
            new ChainSpecificHeader(Hash!, Keccak.OfAnEmptySequenceRlp, Beneficiary!, UInt256.Zero, Number + 1, GasLimit, timestamp, [])
            {
                MixHash = Hash256.Zero,
            };
    }

    /// <summary>
    /// Invokes <c>nextDelta</c> and captures the resulting header.
    /// </summary>
    private sealed class DeltaCapturingPreWarmer : IBlockCachePreWarmer
    {
        public readonly TaskCompletionSource<BlockHeader> CapturedHeader = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PreWarmCaches(Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public CacheType ClearCaches() => default;
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;

        public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
        {
            CapturedHeader.TrySetResult(nextDelta(CancellationToken.None)?.Block.Header);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private static Transaction[] BuildSenderTxs(PrivateKey sender, int count) =>
        Enumerable.Range(0, count)
            .Select(nonce => Build.A.Transaction
                .WithNonce((ulong)nonce)
                .WithTo(TestItem.AddressC)
                .SignedAndResolved(sender)
                .TestObject)
            .ToArray();
}
