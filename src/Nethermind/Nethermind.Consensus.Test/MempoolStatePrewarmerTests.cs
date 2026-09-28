// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
    private const ulong SecondsPerSlot = 12;

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

    /// <summary>
    /// A block arriving cancels the session; selection must stop pulling from the producer's source at once, and a
    /// pass it abandons must not count its senders as warmed, or the next session's pass would skip them.
    /// </summary>
    [Test]
    public void SelectDelta_Cancelled_StopsPullingAndRecordsNothing()
    {
        Transaction[] transactions = BuildSenderTxs(TestItem.PrivateKeyA, 3);
        using CancellationTokenSource cancellation = new();
        Dictionary<AddressAsKey, int> warmed = [];
        Dictionary<AddressAsKey, MempoolStatePrewarmer.SenderSelection> scratch = [];
        bool drained = false;

        Transaction[] delta = MempoolStatePrewarmer.SelectDelta(CancellingSource(), warmed, scratch, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta, Is.Null, "a cancelled pass has no delta");
            Assert.That(drained, Is.False, "selection must stop pulling once cancelled");
            Assert.That(warmed, Is.Empty, "nothing may be recorded as warmed by an abandoned pass");
            Assert.That(scratch, Is.Empty);
        }

        IEnumerable<Transaction> CancellingSource()
        {
            yield return transactions[0];
            cancellation.Cancel();
            yield return transactions[1];
            yield return transactions[2];
            drained = true;
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
        DeltaCapturingPreWarmer preWarmer = new();
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, parentHeader, out IBlockTree blockTree, out ITxSource txSource, out _);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(head));

        BlockHeader deltaHeader = await preWarmer.CapturedHeader.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaHeader, Is.InstanceOf<ChainSpecificHeader>(), "chain-specific header subtypes must survive so chain-specific processors don't hit an InvalidCastException");
            Assert.That(deltaHeader.Number, Is.EqualTo(parentHeader.Number + 1), "the child is the parent's successor");
            Assert.That(deltaHeader.Timestamp, Is.EqualTo(parentHeader.Timestamp + SecondsPerSlot),
                "the predicted header must sit on the next slot boundary: the EIP-4788 cells warmed for it are indexed by its timestamp");
            Assert.That(deltaHeader.MixHash, Is.EqualTo(parentHeader.MixHash), "MixHash is propagated from the parent");
            Assert.That(deltaHeader.ParentBeaconBlockRoot, Is.EqualTo(parentHeader.ParentBeaconBlockRoot), "ParentBeaconBlockRoot is propagated from the parent");
            Assert.That(deltaHeader.BaseFeePerGas, Is.EqualTo(BaseFeeCalculator.Calculate(parentHeader, London.Instance)), "BaseFeePerGas is recalculated for the child");
            Assert.That(deltaHeader.GasBeneficiary, Is.EqualTo(parentHeader.GasBeneficiary), "Beneficiary resolves to the parent's actual coinbase (Author), not a diverging governance vote target");
            txSource.Received(1).GetTransactions(parentHeader, deltaHeader, deltaHeader.GasLimit);
        }
    }

    /// <summary>
    /// The session's token must reach selection: a block queued mid-pass stops the pull from the producer's source, and
    /// the pass yields no delta rather than warming a partial one.
    /// </summary>
    [Test]
    public async Task NextDelta_CancelledDuringSelection_ReturnsNoDelta()
    {
        BlockHeader headHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        Transaction[] transactions = BuildSenderTxs(TestItem.PrivateKeyA, 2);
        using CancellationTokenSource cancellation = new();
        DeltaCapturingPreWarmer preWarmer = new() { DeltaToken = cancellation.Token };
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, headHeader, out IBlockTree blockTree, out ITxSource txSource, out _);
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns(CancellingSource());

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(headHeader)));

        Assert.That(await preWarmer.CapturedHeader.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.Null, "a pass cancelled during selection has no delta");

        IEnumerable<Transaction> CancellingSource()
        {
            yield return transactions[0];
            cancellation.Cancel();
            yield return transactions[1];
        }
    }

    /// <summary>
    /// A block queued for processing has its processing scope join the running session, so the session is cancelled
    /// when the block is queued and drains while it is recovered. A suggestion alone may never be processed, so it must
    /// not stop warming.
    /// </summary>
    [Test]
    public void QueuedBlock_CancelsTheRunningSession_SuggestionAloneDoesNot()
    {
        BlockHeader headHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        TokenCapturingPreWarmer preWarmer = new();
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, headHeader, out IBlockTree blockTree, out _, out IBlockProcessingQueue queue);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(headHeader)));
        CancellationToken session = preWarmer.NextSession();

        Block child = Build.A.Block.WithNumber(11).WithParent(headHeader).TestObject;
        blockTree.NewSuggestedBlock += Raise.EventWith(new BlockEventArgs(child));
        bool cancelledBySuggestion = session.IsCancellationRequested;
        queue.BlockAdded += Raise.EventWith(new BlockEventArgs(child));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancelledBySuggestion, Is.False, "a suggestion alone must leave the session running");
            Assert.That(session.IsCancellationRequested, Is.True, "a block queued for processing must cancel the running session");
        }
    }

    [Test]
    public void Dispose_CancelsTheRunningSession()
    {
        BlockHeader headHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        TokenCapturingPreWarmer preWarmer = new();
        MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, headHeader, out IBlockTree blockTree, out _, out _);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(headHeader)));
        CancellationToken session = preWarmer.NextSession();
        prewarmer.Dispose();

        Assert.That(session.IsCancellationRequested, Is.True);
    }

    /// <summary>
    /// A newer head's session replaces the running one, which must stop at once: only the newest session is cancelled
    /// when a block is queued, and starting the new session does not join the old one when the new spec skips warming.
    /// </summary>
    [Test]
    public void NewHead_CancelsThePreviousSession()
    {
        BlockHeader oldHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        BlockHeader newHeader = Build.A.BlockHeader.WithNumber(11).WithTimestamp(100).WithGasLimit(30_000_000).WithParent(oldHeader).TestObject;
        TokenCapturingPreWarmer preWarmer = new();
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, oldHeader, out IBlockTree blockTree, out _, out _);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(oldHeader)));
        CancellationToken first = preWarmer.NextSession();
        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(newHeader)));
        CancellationToken second = preWarmer.NextSession();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsCancellationRequested, Is.True, "the replaced session must be cancelled");
            Assert.That(second.IsCancellationRequested, Is.False, "the new session must be running");
        }
    }

    /// <summary>
    /// A queued block cancels the session, but may leave the queue without success, some before any processing scope
    /// opens; no new head follows, so warming must start again from the unchanged head. A processed block is followed
    /// by its own head's session instead.
    /// </summary>
    [Test]
    public void RemovedBlock_WithoutSuccess_StartsWarmingAgain(
        [Values(ProcessingResult.Success, ProcessingResult.InclusionListUnsatisfied, ProcessingResult.QueueException, ProcessingResult.MissingBlock)] ProcessingResult result)
    {
        BlockHeader headHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        Block head = new(headHeader);
        TokenCapturingPreWarmer preWarmer = new();
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, headHeader, out IBlockTree blockTree, out _, out IBlockProcessingQueue queue);
        blockTree.Head.Returns(head);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(head));
        CancellationToken first = preWarmer.NextSession();
        Block child = Build.A.Block.WithNumber(11).WithParent(headHeader).TestObject;
        queue.BlockAdded += Raise.EventWith(new BlockEventArgs(child));
        queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(child.Hash!, result));

        bool expectRestart = result is not (ProcessingResult.Success or ProcessingResult.InclusionListUnsatisfied or ProcessingResult.QueueException);
        bool restarted = preWarmer.TryNextSession(expectRestart ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200), out CancellationToken second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsCancellationRequested, Is.True, "precondition: queueing the block cancels the session");
            Assert.That(restarted, Is.EqualTo(expectRestart), "only a block dropped by a working queue restarts warming");
            if (restarted) Assert.That(second.IsCancellationRequested, Is.False, "the new session must be running");
        }
    }

    /// <summary>
    /// A head published while a failed removal is restarting warming must keep its own session: the restart read the
    /// old head, and letting it take a newer generation would cancel the new head's warming for the old head's.
    /// </summary>
    [Test]
    public void RemovedBlock_NewHeadDuringRestart_KeepsTheNewHeadsSession()
    {
        BlockHeader oldHeader = Build.A.BlockHeader.WithNumber(10).WithTimestamp(100).WithGasLimit(30_000_000).TestObject;
        BlockHeader newHeader = Build.A.BlockHeader.WithNumber(11).WithTimestamp(100).WithGasLimit(30_000_000).WithParent(oldHeader).TestObject;
        TokenCapturingPreWarmer preWarmer = new();
        using MempoolStatePrewarmer prewarmer = CreatePrewarmer(preWarmer, oldHeader, out IBlockTree blockTree, out _, out IBlockProcessingQueue queue);

        blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(oldHeader)));
        preWarmer.NextSession();

        // The restart reads the old head, and the new head is published right after that read.
        int headReads = 0;
        blockTree.Head.Returns(_ =>
        {
            if (Interlocked.Increment(ref headReads) == 1) blockTree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(newHeader)));
            return new Block(oldHeader);
        });
        Block child = Build.A.Block.WithNumber(11).WithParent(oldHeader).TestObject;
        queue.BlockRemoved += Raise.EventWith(new BlockRemovedEventArgs(child.Hash!, ProcessingResult.MissingBlock));

        (BlockHeader started, _) = preWarmer.NextSessionWithHead();
        bool another = preWarmer.TryNextSession(TimeSpan.FromMilliseconds(200), out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.SameAs(newHeader), "the new head's session must be the one started");
            Assert.That(another, Is.False, "the restart must not start a session for the old head as well");
        }
    }

    /// <summary>A prewarmer whose clock reads the head's timestamp, so the head is fresh enough to warm from.</summary>
    private static MempoolStatePrewarmer CreatePrewarmer(IBlockCachePreWarmer preWarmer, BlockHeader head, out IBlockTree blockTree,
        out ITxSource txSource, out IBlockProcessingQueue processingQueue)
    {
        txSource = Substitute.For<ITxSource>();
        txSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns(BuildSenderTxs(TestItem.PrivateKeyA, 1));
        IBlockProducerTxSourceFactory txSourceFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        txSourceFactory.Create().Returns(txSource);

        blockTree = Substitute.For<IBlockTree>();
        IBlockProcessingQueue queue = processingQueue = Substitute.For<IBlockProcessingQueue>();

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(London.Instance);

        DateTime headTime = DateTimeOffset.FromUnixTimeSeconds((long)head.Timestamp).UtcDateTime;
        ITimestamper timestamper = Substitute.For<ITimestamper>();
        timestamper.UtcNow.Returns(headTime);
        timestamper.UnixTime.Returns(new UnixTime(headTime));

        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.PreWarming.Returns(PreWarmMode.BlockAndMempool);
        blocksConfig.SecondsPerSlot.Returns(SecondsPerSlot);

        return new MempoolStatePrewarmer(preWarmer, txSourceFactory, blockTree, new Lazy<IBlockProcessingQueue>(() => queue), specProvider, timestamper, blocksConfig, LimboLogs.Instance);
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
        public CancellationToken DeltaToken { get; init; }

        public IDisposable PreWarmCaches(Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken = default) => null;
        public CacheType ClearCaches() => default;
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;

        public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
        {
            CapturedHeader.TrySetResult(nextDelta(DeltaToken)?.Block.Header);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    /// <summary>Captures the head and token each session is started with, without running it.</summary>
    private sealed class TokenCapturingPreWarmer : IBlockCachePreWarmer
    {
        private readonly BlockingCollection<(BlockHeader Head, CancellationToken Token)> _sessions = [];

        public CancellationToken NextSession() => NextSessionWithHead().Token;

        public (BlockHeader Head, CancellationToken Token) NextSessionWithHead() =>
            _sessions.TryTake(out (BlockHeader, CancellationToken) session, TimeSpan.FromSeconds(5)) ? session : throw new TimeoutException("no session was started");

        public bool TryNextSession(TimeSpan timeout, out CancellationToken token)
        {
            bool started = _sessions.TryTake(out (BlockHeader Head, CancellationToken Token) session, timeout);
            token = session.Token;
            return started;
        }

        public IDisposable PreWarmCaches(Block suggestedBlock, BlockHeader parent, IReleaseSpec spec, CancellationToken cancellationToken = default) => null;
        public CacheType ClearCaches() => default;
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;

        public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, (Block Block, IReleaseSpec Spec)?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken)
        {
            _sessions.Add((head, cancellationToken));
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
