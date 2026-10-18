// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Int256;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class ProofWrapperEventFeedTests
{
    private static ProofWrapperEvent Event(string type = ProofWrapperEventTypes.Announced) => new() { Type = type };

    [Test]
    public void Disabled_feed_records_nothing_and_subscribes_to_nothing()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ITxPool txPool = Substitute.For<ITxPool>();
        using ProofWrapperEventFeed feed = new(0, blockTree, txPool);
        feed.Record(Event());
        ProofWrapperEventPage page = feed.Read(0, 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.IsEnabled, Is.False);
            Assert.That(ProofWrapperEventFeed.Disabled.IsEnabled, Is.False);
            Assert.That(page.Events, Is.Empty);
            Assert.That(page.Latest, Is.Zero);
            Assert.That(feed.Describe([0xc0]), Is.Null, "a disabled feed never parses wrappers");
        }
        blockTree.DidNotReceiveWithAnyArgs().BlockAddedToMain += null;
        txPool.DidNotReceiveWithAnyArgs().EvictedPending += null;
    }

    [TestCase(4, 10, 0, 7, 6)]
    [TestCase(4, 10, 8, 9, 0)]
    [TestCase(4, 3, 0, 1, 0)]
    [TestCase(4, 10, 10, 11, 0)]
    public void Ring_buffer_keeps_the_newest_events_and_reports_overwritten_ones(int capacity, int recorded, long after, long expectedFirst, long expectedMissed)
    {
        ProofWrapperEventFeed feed = new(capacity);
        for (int i = 0; i < recorded; i++) feed.Record(Event());
        ProofWrapperEventPage page = feed.Read(after, 100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Latest, Is.EqualTo(recorded));
            Assert.That(page.Capacity, Is.EqualTo(capacity));
            Assert.That(page.Missed, Is.EqualTo(expectedMissed));
            Assert.That(page.Events.Select(e => e.Seq), Is.EqualTo(Enumerable.Range((int)expectedFirst, Math.Max(0, recorded - (int)expectedFirst + 1)).Select(i => (long)i)));
        }
    }

    [Test]
    public void Pages_are_bounded_and_continue_from_the_last_sequence_number()
    {
        ProofWrapperEventFeed feed = new(5_000);
        for (int i = 0; i < 4_000; i++) feed.Record(Event());
        ProofWrapperEventPage first = feed.Read(0, 3);
        ProofWrapperEventPage capped = feed.Read(0, int.MaxValue);
        ProofWrapperEventPage next = feed.Read(first.Events[^1].Seq, 3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Events.Select(e => e.Seq), Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(capped.Events, Has.Length.EqualTo(2_000), "one call never returns more than the page limit");
            Assert.That(next.Events.Select(e => e.Seq), Is.EqualTo(new long[] { 4, 5, 6 }));
            Assert.That(feed.Read(0, 0).Events, Has.Length.EqualTo(1), "the limit is at least one");
        }
    }

    [Test]
    public void Events_carry_increasing_sequence_numbers_and_the_record_time()
    {
        ManualTimeProvider clock = new();
        clock.Advance(TimeSpan.FromSeconds(1_000));
        ProofWrapperEventFeed feed = new(8, clock);
        feed.Record(Event(ProofWrapperEventTypes.WrapperRequested));
        clock.Advance(TimeSpan.FromMilliseconds(12.5));
        feed.Record(Event(ProofWrapperEventTypes.WrapperReceived));
        ProofWrapperEvent[] events = feed.Read(0, 10).Events;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { ProofWrapperEventTypes.WrapperRequested, ProofWrapperEventTypes.WrapperReceived }));
            Assert.That(events.Select(e => e.Seq), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(events[0].Time, Is.EqualTo(1_000_000));
            Assert.That(events[1].Time - events[0].Time, Is.EqualTo(12.5).Within(0.001));
        }
    }

    [Test]
    public void Describes_a_wrapper_by_mode_transactions_and_dependencies()
    {
        ProofWrapperEventFeed feed = new(8);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute([1]), ValueKeccak.Compute([2]));
        Hash256 transaction = TestItem.KeccakA;
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ProofWrapperDetails details = feed.Describe(wrapper);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(details, Is.Not.Null);
            Assert.That(details.Mode, Is.Zero);
            Assert.That(details.Length, Is.EqualTo(wrapper.Length));
            Assert.That(details.Transactions, Is.EqualTo(new[] { transaction }));
            Assert.That(details.Dependencies, Is.EqualTo(new[] { $"leanSPHINCS:{dependency.DataHash}:{dependency.VerificationKey}" }));
            Assert.That(feed.Describe([0xc0]), Is.Null, "a malformed wrapper has no description");
        }
    }

    [Test]
    public void Records_inclusion_and_pool_eviction_of_dependency_bearing_transactions_only()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ITxPool txPool = Substitute.For<ITxPool>();
        using ProofWrapperEventFeed feed = new(8, blockTree, txPool);
        Transaction bearing = DependencyBearing();
        Transaction plain = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        Block block = Build.A.Block.WithNumber(7).WithTransactions(bearing, plain).TestObject;

        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        txPool.EvictedPending += Raise.Event<EventHandler<TxEventArgs>>(txPool, new TxEventArgs(bearing));
        txPool.EvictedPending += Raise.Event<EventHandler<TxEventArgs>>(txPool, new TxEventArgs(plain));
        feed.Dispose();
        blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        ProofWrapperEvent[] events = feed.Read(0, 10).Events;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { ProofWrapperEventTypes.Included, ProofWrapperEventTypes.PoolEvicted }));
            Assert.That(events[0].Block, Is.EqualTo(7));
            Assert.That(events[0].BlockHash, Is.EqualTo(block.Hash));
            Assert.That(events.Select(e => e.Transactions!.Single()), Is.All.EqualTo(bearing.Hash));
        }
    }

    private static Transaction DependencyBearing()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute([3]), default);
        return new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Hash = TestItem.KeccakC,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
    }
}
