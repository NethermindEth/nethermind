// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class PreparedCacheTests
{
    private static readonly ByteString Identity = ByteString.CopyFrom(TestItem.KeccakA.Bytes);

    [Test]
    public void Borrow_MissingFact_ReturnsTypedRepairDetails()
    {
        PreparedOrdinaryBlock fact = Fact(1);
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30));

        RpcException exception = Assert.Throws<RpcException>(() => cache.Borrow(Range(fact)))!;

        ProveFailure failure = ProveFailure.Parser.ParseFrom(exception.Trailers.GetValueBytes("grpc-status-details-bin"));
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
        Assert.That((failure.CacheMiss.ValidationIdentity, failure.CacheMiss.BlockNumber, failure.CacheMiss.BlockHash),
            Is.EqualTo((Identity, fact.Number, ByteString.CopyFrom(fact.Hash.Bytes))));
    }

    [Test]
    public void Publish_CapacityFailure_IsAtomic()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxEntries: 1);
        PreparedOrdinaryBlock first = Fact(1), second = Fact(2, first);
        ByteString lease = cache.Publish([first], ByteString.Empty, default);

        RpcException exception = Assert.Throws<RpcException>(() => cache.Publish([second], lease, default))!;

        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.ResourceExhausted));
        using PreparedCache.BorrowedRange retained = cache.Borrow(Range(first));
        Assert.That(retained.Facts, Is.EqualTo(new[] { first }));
        Assert.That(() => cache.Borrow(Range(second)), Throws.TypeOf<RpcException>());
    }

    [Test]
    public void Publish_ActiveBorrow_SurvivesReleaseAndBlocksEviction()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxEntries: 1);
        PreparedOrdinaryBlock first = Fact(1), second = Fact(2, first);
        ByteString lease = cache.Publish([first], ByteString.Empty, default);
        PreparedCache.BorrowedRange borrowed = cache.Borrow(Range(first));
        Release(cache, lease);

        Assert.That(() => cache.Publish([second], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        borrowed.Dispose();
        borrowed.Dispose();
        cache.Publish([second], ByteString.Empty, default);

        Assert.That(() => cache.Borrow(Range(first)), Throws.TypeOf<RpcException>());
        using PreparedCache.BorrowedRange replacement = cache.Borrow(Range(second));
        Assert.That(replacement.Facts[0], Is.SameAs(second));
    }

    [Test]
    public void Retain_LeaseExpiry_DoesNotDiscardActiveFact()
    {
        Clock clock = new();
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxEntries: 1, clock: clock);
        PreparedOrdinaryBlock fact = Fact(1), second = Fact(2, fact);
        ByteString lease = cache.Publish([fact], ByteString.Empty, default);
        using PreparedCache.BorrowedRange borrowed = cache.Borrow(Range(fact));
        clock.Advance(TimeSpan.FromSeconds(120));

        Assert.That(() => Release(cache, lease), Throws.TypeOf<RpcException>());
        Assert.That(() => cache.Publish([second], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        Assert.That(borrowed.Facts[0].Materialize().Hash, Is.EqualTo(fact.Hash));
    }

    [Test]
    public void Retain_ReplacesExactSet_AndPrepareExtendsLease()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxEntries: 2);
        PreparedOrdinaryBlock first = Fact(1), second = Fact(2, first), third = Fact(3, second);
        ByteString lease = cache.Publish([first], ByteString.Empty, default);
        Assert.That(cache.Publish([second], lease, default), Is.EqualTo(lease));
        RetainOrdinaryRangesRequest retain = new() { ValidationIdentity = Identity, LeaseId = lease };
        retain.Ranges.Add(Range(second));

        Assert.That(cache.Retain(retain, default).RetainedCount, Is.EqualTo(1));
        cache.Publish([third], lease, default);

        Assert.That(() => cache.Borrow(Range(first)), Throws.TypeOf<RpcException>());
        using PreparedCache.BorrowedRange remaining = cache.Borrow(Range(second, third));
        Assert.That(remaining.Facts, Is.EqualTo(new[] { second, third }));
    }

    [Test]
    public void Retain_InvalidRange_PreservesReservations()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxEntries: 1);
        PreparedOrdinaryBlock first = Fact(1), second = Fact(2, first);
        ByteString lease = cache.Publish([first], ByteString.Empty, default);
        RetainOrdinaryRangesRequest request = new() { ValidationIdentity = Identity, LeaseId = lease };
        request.Ranges.Add(Range(second));

        Assert.That(() => cache.Retain(request, default), Throws.TypeOf<RpcException>());
        Assert.That(() => cache.Publish([second], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        using PreparedCache.BorrowedRange borrowed = cache.Borrow(Range(first));
        Assert.That(borrowed.Facts[0], Is.SameAs(first));
    }

    [Test]
    public void Publish_MaximumLeases_RejectsWithoutEviction()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30));
        PreparedOrdinaryBlock fact = Fact(1);
        for (int i = 0; i < PreparedCache.MaximumLeases; i++) cache.Publish([fact], ByteString.Empty, default);

        Assert.That(() => cache.Publish([fact], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        using PreparedCache.BorrowedRange borrowed = cache.Borrow(Range(fact));
        Assert.That(borrowed.Facts[0], Is.SameAs(fact));
    }

    [Test]
    public void Publish_Cancelled_DoesNotPublish()
    {
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30));
        PreparedOrdinaryBlock fact = Fact(1);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.That(() => cache.Publish([fact], ByteString.Empty, cancellation.Token), Throws.TypeOf<OperationCanceledException>());
        Assert.That(() => cache.Borrow(Range(fact)), Throws.TypeOf<RpcException>());
    }

    [Test]
    public void Publish_ByteLimit_CountsRetainedMetadata()
    {
        PreparedOrdinaryBlock fact = Fact(1);
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30), maxBytes: fact.RetainedBytes - 1);

        Assert.That(() => cache.Publish([fact], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        Assert.That(() => cache.Borrow(Range(fact)), Throws.TypeOf<RpcException>());
    }

    [Test]
    public void Publish_RangeStateMismatch_Rejects()
    {
        PreparedOrdinaryBlock first = Fact(1), second = Fact(2, first, incorrectPreState: true);
        PreparedCache cache = new(Identity, TimeSpan.FromSeconds(30));
        Assert.That(() => cache.Publish([first, second], ByteString.Empty, default), Throws.TypeOf<RpcException>());
        Assert.That(() => cache.Borrow(Range(first)), Throws.TypeOf<RpcException>());
    }

    [Test]
    public void Materialize_MutatedResult_DoesNotChangeStoredFact()
    {
        PreparedOrdinaryBlock fact = Fact(1);
        EezStatelessBlockResult result = fact.Materialize();
        result.Block.Header.StateRoot = TestItem.KeccakC;
        result.Parent.StateRoot = TestItem.KeccakD;

        EezStatelessBlockResult again = fact.Materialize();

        Assert.That(again.Block.Header.StateRoot, Is.EqualTo(fact.PostStateRoot));
        Assert.That(again.Parent.StateRoot, Is.EqualTo(fact.PreStateRoot));
    }

    [Test]
    public void Create_SystemTransaction_Rejects()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.WithType(EezConstants.SystemTxType)
            .WithSenderAddress(EezConstants.SystemAddress).TestObject).TestObject;

        Assert.That(() => PreparedOrdinaryBlock.Create(new(block, block.Header, [], [])), Throws.TypeOf<EezSettlementException>());
    }

    private static PreparedOrdinaryBlock Fact(ulong number, PreparedOrdinaryBlock? previous = null, bool incorrectPreState = false)
    {
        BlockHeader parent = Build.A.Block.WithNumber(number - 1).WithStateRoot(previous?.PostStateRoot ?? TestItem.KeccakB).TestObject.Header;
        byte[] root = new byte[32];
        root[^1] = (byte)number;
        Block block = Build.A.Block.WithNumber(number).WithParentHash(previous?.Hash ?? parent.Hash!)
            .WithStateRoot(new Hash256(root))
            .TestObject;
        if (incorrectPreState) parent.StateRoot = TestItem.KeccakD;
        return PreparedOrdinaryBlock.Create(new(block, parent, [], []));
    }

    private static CachedOrdinaryRange Range(PreparedOrdinaryBlock first, PreparedOrdinaryBlock? last = null) => new()
    {
        ValidationIdentity = Identity,
        FirstNumber = first.Number,
        FirstHash = ByteString.CopyFrom(first.Hash.Bytes),
        LastNumber = (last ?? first).Number,
        LastHash = ByteString.CopyFrom((last ?? first).Hash.Bytes),
    };

    private static void Release(PreparedCache cache, ByteString lease) =>
        cache.Retain(new RetainOrdinaryRangesRequest { ValidationIdentity = Identity, LeaseId = lease, Release = true }, default);

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
