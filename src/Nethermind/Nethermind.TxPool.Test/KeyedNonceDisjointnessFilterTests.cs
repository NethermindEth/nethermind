// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>EIP-8250 disjointness: two pending keyed-nonce frame transactions of one sender may not share a nonce key.</summary>
public class KeyedNonceDisjointnessFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly UInt256 Hub = 0xaaa;
    private static readonly UInt256 K1 = 0x111;
    private static readonly UInt256 K2 = 0x222;

    [TestCaseSource(nameof(KeyedCases))]
    public void Accept_KeyedFrameTransaction_IsRejectedOnlyOnAnOverlappingKeySet(UInt256[] incoming, UInt256[] pending, AcceptTxResult expected)
    {
        AcceptTxResult result = Accept(KeyedTx(incoming), PendingWith(pending));

        Assert.That(result, Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> KeyedCases()
    {
        yield return new TestCaseData(new[] { Hub, K2 }, new[] { Hub, K1 }, AcceptTxResult.KeyedNonceOverlap).SetName("overlapping key set is rejected");
        yield return new TestCaseData(new[] { UInt256.Zero, K2 }, new[] { UInt256.Zero, K1 }, AcceptTxResult.KeyedNonceOverlap).SetName("overlap on key 0 is rejected");
        yield return new TestCaseData(new[] { K2 }, new[] { K1 }, AcceptTxResult.Accepted).SetName("disjoint key sets are admitted");
        yield return new TestCaseData(new[] { K1 }, new[] { K1 }, AcceptTxResult.Accepted).SetName("same competing slot is not an overlap");
    }

    [Test]
    public void Accept_SortedKeySets_MatchesSetIntersection(
        [Values(1, 2, 4, 8, 16, 17)] int incomingCount,
        [Values(1, 2, 4, 8, 16, 17)] int pendingCount,
        [Values] bool overlap)
    {
        UInt256[] incoming = new UInt256[incomingCount];
        UInt256[] pending = new UInt256[pendingCount];
        for (int i = 0; i < incoming.Length; i++) incoming[i] = (UInt256)(2 * i);
        for (int i = 0; i < pending.Length; i++) pending[i] = (UInt256)(2 * i + 1);
        if (overlap)
        {
            pending[^1] = incoming[^1];
            Array.Sort(pending);
        }

        AcceptTxResult expected = new HashSet<UInt256>(incoming).Overlaps(pending)
            ? AcceptTxResult.KeyedNonceOverlap
            : AcceptTxResult.Accepted;

        AcceptTxResult result = Accept(FrameTx(incoming, nonce: 1), PendingWith(pending));

        Assert.That(result, Is.EqualTo(expected), "different nonce sequences must still reject intersecting key sets");
    }

    [TestCaseSource(nameof(DirectInputCases))]
    public void Accept_DirectKeySets_PreservesIntersection(UInt256[] incoming, UInt256[] pending, AcceptTxResult expected)
    {
        UInt256[] originalIncoming = (UInt256[])incoming.Clone();
        UInt256[] originalPending = (UInt256[])pending.Clone();

        AcceptTxResult result = Accept(FrameTx(incoming, nonce: 1), PendingWith(pending));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected), "direct filter calls retain their intersection result");
            Assert.That(incoming, Is.EqualTo(originalIncoming), "the filter must not reorder the incoming transaction's keys");
            Assert.That(pending, Is.EqualTo(originalPending), "the filter must not reorder the pending transaction's keys");
        }
    }

    private static IEnumerable<TestCaseData> DirectInputCases()
    {
        yield return new TestCaseData(new UInt256[] { 3, 1, 5 }, new UInt256[] { 1, 2, 4 }, AcceptTxResult.KeyedNonceOverlap)
            .SetName("unordered incoming and sorted pending retain overlap");
        yield return new TestCaseData(new UInt256[] { 8, 1, 9 }, new UInt256[] { 2, 3, 1 }, AcceptTxResult.KeyedNonceOverlap)
            .SetName("overlap after descending keys is rejected");
        yield return new TestCaseData(new UInt256[] { 9, 5, 1 }, new UInt256[] { 8, 4, 2 }, AcceptTxResult.Accepted)
            .SetName("disjoint descending keys are accepted");
        yield return new TestCaseData(new UInt256[] { 1, 2, 3 }, new UInt256[] { 5, 4, 2 }, AcceptTxResult.KeyedNonceOverlap)
            .SetName("sorted incoming and unordered pending retain overlap");
        yield return new TestCaseData(new UInt256[] { 1, 1, 9 }, new UInt256[] { 2, 3, 9 }, AcceptTxResult.KeyedNonceOverlap)
            .SetName("duplicate direct-input keys retain overlap");
        yield return new TestCaseData(new UInt256[] { 0, 1, UInt256.MaxValue }, new UInt256[] { 2, 3, UInt256.MaxValue }, AcceptTxResult.KeyedNonceOverlap)
            .SetName("overlap on the largest key is rejected");
    }

    [TestCaseSource(nameof(UngatedCases))]
    public void Accept_NonKeyedFrameTransactions_AreUntouched(System.Func<Transaction> build)
    {
        AcceptTxResult result = Accept(build(), PendingWith([K1]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static IEnumerable<TestCaseData> UngatedCases()
    {
        yield return new TestCaseData(() => FrameTx(null)).SetName("frame transaction on the account nonce");
        yield return new TestCaseData(() => FrameTx([])).SetName("frame transaction with an empty key set");
        yield return new TestCaseData(() => Build.A.Transaction.WithNonce(0).WithSenderAddress(Sender).TestObject).SetName("plain transaction");
    }

    private static AcceptTxResult Accept(Transaction tx, TxDistinctSortedPool pending)
    {
        KeyedNonceDisjointnessFilter filter = new(pending, FrameTxFilterTestPools.Pool(false));
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static TxDistinctSortedPool PendingWith(UInt256[] keys) => FrameTxFilterTestPools.Pool(false, KeyedTx(keys));

    private static Transaction KeyedTx(UInt256[] keys) => FrameTx(keys);

    private static Transaction FrameTx(UInt256[]? nonceKeys, ulong nonce = 0)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Sender,
            Nonce = nonce,
            NonceKeys = nonceKeys,
            Frames = [],
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
