// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
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

    [Test]
    public void Accept_OverlappingKeySet_IsRejected()
    {
        AcceptTxResult result = Accept(KeyedTx([Hub, K2]), PendingWith([Hub, K1]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.KeyedNonceOverlap));
    }

    [Test]
    public void Accept_DisjointKeySets_AreAdmitted()
    {
        AcceptTxResult result = Accept(KeyedTx([K2]), PendingWith([K1]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SameCompetingSlot_IsNotAnOverlap()
    {
        AcceptTxResult result = Accept(KeyedTx([K1]), PendingWith([K1]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
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
        KeyedNonceDisjointnessFilter filter = new(pending, Pool());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static TxDistinctSortedPool PendingWith(UInt256[] keys) => Pool(KeyedTx(keys));

    private static TxDistinctSortedPool Pool(params Transaction[] pending)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec { IsEip1559Enabled = false });
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);
        IComparer<Transaction> comparer = new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer();
        TxDistinctSortedPool pool = new(pending.Length + 1, comparer, LimboLogs.Instance);
        foreach (Transaction tx in pending)
        {
            pool.TryInsert(tx.Hash!, tx);
        }

        return pool;
    }

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
