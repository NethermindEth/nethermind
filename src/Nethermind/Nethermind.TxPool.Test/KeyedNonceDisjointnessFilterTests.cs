// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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
        yield return new TestCaseData(new[] { K2 }, new[] { K1 }, AcceptTxResult.Accepted).SetName("disjoint key sets are admitted");
        yield return new TestCaseData(new[] { K1 }, new[] { K1 }, AcceptTxResult.Accepted).SetName("same competing slot is not an overlap");
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
        yield return new TestCaseData(() => FrameTx([UInt256.Zero])).SetName("frame transaction aliasing the account nonce");
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

    private static Transaction FrameTx(UInt256[]? nonceKeys)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Sender,
            Nonce = 0,
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
